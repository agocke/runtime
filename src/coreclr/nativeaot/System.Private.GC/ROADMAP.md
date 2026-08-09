# NativeAOT managed GC roadmap

This document tracks the dependency-ordered port of the NativeAOT garbage collector from
`src/coreclr/gc` to unsafe C# in `System.Private.GC`.

The existing bootable managed heap is a validation scaffold, not the target design. It proves
that a NativeAOT process can link, initialize, allocate, create handles, publish write-barrier
state, and suspend the runtime using managed GC entrypoints. As each real GC module is ported,
the corresponding scaffold code must be replaced by the mechanical translation of the C++
implementation.

## Translation contract

The C# implementation must remain directly comparable with the C++ implementation:

- Port files and functions one-for-one whenever practical. Preserve function order, control
  flow, pointer arithmetic, constants, and comments that explain GC invariants.
- Preserve C++ names, including `snake_case`, for translated types, fields, and functions.
  Managed-only adapters may use normal .NET naming.
- Do not redesign algorithms while translating them. Algorithmic changes should first be made
  in the C++ GC, then ported to C# in a separate, reviewable change.
- Keep heap addresses as `byte*` or `nuint`. GC code must not allocate managed objects or hold
  managed references.
- Avoid exceptions, reflection, type loading, lazy static initialization, managed interface
  dispatch, `string`, LINQ, and reference-type generics.
- Preserve native layout and GC/EE ABI behavior. Managed-only mechanisms such as
  `GCHeapCriticalRegion`, `RuntimeImport`, and `SuppressGCTransition` are acceptable only when
  they reproduce behavior that the C++ implementation receives from native execution.
- Keep the C++ and C# sources synchronized until the managed GC becomes the shared
  implementation.

## Current bootstrap status

The following prerequisites are already working:

- `System.Private.GC` is built into the NativeAOT SDK and selected with `IlcManagedGC=true`.
- A NativeAOT application can boot and run entirely on the managed heap. Managed-GC
  initialization failures fail startup rather than selecting the C++ GC.
- Managed-GC applications link `Runtime.ManagedGC` and do not include the C++ workstation or
  server collector, native handle table, GC loader, bridge, scanner, or software write watch.
  Native runtime and `gcenv` support remains temporarily; the environment services the managed
  layer has not taken over itself are reached through the documented `ManagedGC_*` forwarders of
  `nativeaot/Runtime/gcenv.managed.cpp`. Virtual memory, write watch, events, locks, sleep and
  yield, and the memory limits and cache sizing are no longer among them.
- The current heap allocates from translated WKS regions with one translated handle table and
  routes explicitly requested synchronous foreground Gen0, Gen1, and Gen2 collections.
- The managed GC reads its own configuration: `GCConfig` is translated in full, initialized from
  `ManagedGC_Initialize`, and reported to `GC.GetConfigurationVariables()` through the heap's
  `EnumerateConfigurationValues` slot.
- Write-barrier globals and frozen segments are initialized sufficiently for application
  startup.
- `GC.Collect` routes the bounded WKS `USE_REGIONS` synchronous foreground lifecycle.
  Forced non-blocking Gen2 also routes when concurrent GC is configured; optimized/aggressive,
  server, heap-verification, and survivor-analysis modes remain rejected before collector
  mutation. Public and private collection event modes are routed.
- Managed GC mutations use suspension-safe critical regions.
- GC/EE structure layouts, enum values and sizes, and all six vtable slot lists -- with their
  signatures and calling conventions -- are verified against the native headers.

These items validate integration but do not mark the corresponding collector modules complete.

## Dependency-ordered work

### 1. Foundations

**Status: Complete**

Port the project foundations and dependency-free leaves:

- `introsort.h`
- `gceventstatus.h` and `gceventstatus.cpp`
- GC event enums from `gcinterface.h`
- Porting conventions, build wiring, and source-to-source mapping

**Complete when:** every dependency-free leaf used by NativeAOT has a mechanically comparable
C# implementation with focused tests.

All three leaves are translated, and `tests/ManagedGC.Foundation.Tests.csproj` drives each of
them directly as regular xUnit tests, independently of the NativeAOT runtime integration smoke
test. The event enumerator values are checked against the C++ enums by the
`GCInterfaceOffsets.h` table, in the same way as the interface struct layouts.

Two pieces of `gceventstatus.h` are deliberately deferred rather than missing: `DebugDumpState`
needs a string-free logging facility and belongs with GC tracing in stage 11, and
`FireDynamicEvent` plus the `KNOWN_EVENT`/`DYNAMIC_EVENT` macro expansions need `gcevents.h` and
`gcevent_serializers.h` from stage 4. Publishing the initial event status to the EE with
`GCToEEInterface::UpdateGCEventStatus`, which `init.cpp` and `collect.cpp` do, arrives with the
collector modules that contain those call sites.

### 2. GC/EE interface surface

**Status: Complete, except for the DAC types that depend on later stages**

Translate:

- `gcinterface.h`
- `gcinterface.ee.h`
- `gcinterface.dac.h`
- `IGCHeap`, `IGCHandleStore`, `IGCHandleManager`, `IGCToCLR`,
  `IGCHeapInternal`, and `IGCToCLREventSink`
- Shared structures such as `gc_alloc_context`, `segment_info`, `WriteBarrierParameters`,
  `ScanContext`, `EtwGCSettingsInfo`, `MarkCrossReferencesArgs`, `VersionInfo`, and `GcDacVars`

The managed representation uses function-pointer vtables. Native EE callbacks preserve the
cooperative-mode behavior of direct C++ calls.

**Complete when:** all interface methods, signatures, slot order, structure sizes, and field
offsets are automatically checked against the native headers on every supported architecture.

All six interfaces are translated, and the object the GC hands to the EE is an
`IGCHeapInternal`, matching `GCHeap`. Every slot of every interface is checked against the
native headers at build time by `tools/verify-gc-interface-vtables.py` -- count, order, name,
signature, and calling convention -- and every translated structure, enum and constant is pinned
by `GCInterfaceOffsets.h`, which is asserted against the C++ headers by the native build, against
the managed types at GC startup, and against the managed types again by
`tests/GCInterfaceLayoutTests.cs`, which also fails if the table has stopped being complete.
Because the table is expanded by the native build for the architecture being built, the checks
cover every supported architecture.

One group of `gcinterface.dac.h` types is deferred rather than missing. `dac_generation` and
`dac_gc_heap` are generated from the `dac_generation_fields.h` / `dac_gcheap_fields.h` field
lists, which name `gcpriv.h` types and therefore belong with stage 6. The handle-table DAC
analogues arrived with the stage-5 constants and packed segment schema.
`PopulateDacVars` itself belongs with stage 11: it publishes the addresses of the collector's
data structures, none of which exist yet.

`IGCToCLREventSink` is translated as a vtable, but the GC has no call site for it yet. The C++
GC reaches it through the `FIRE_EVENT` macro of `gceventstatus.h`, which expands the event list
in `gcevents.h`; that arrives with the rest of the event plumbing in stage 4.

### 3. `gcenv` and platform abstraction layer

**Status: In progress -- the interface is complete, virtual memory, write watch, events and
locks are translated, the rest of the platform implementations are not**

Translate:

- `GCToOSInterface`
- `GCToEEInterface`
- `Interlocked` and `Volatile` helpers
- `gcenv.structs.h`
- Virtual memory, affinity, NUMA, hard-limit, timer, and thread support

The first version may use allocation-free direct calls into the existing native
`gcenv.unix.cpp`, `gcenv.windows.cpp`, and `nativeaot/Runtime/gcenv.ee.cpp`. Those shims should
be removed as their implementations are ported.

**Complete when:** every environment service used by the collector is available with the same
semantics as C++, including suspension-safe calls made while the runtime is stopped.

#### Done

- `env/gcenv.base.h`: alignment helpers, bit scans, HRESULT helpers, `FitsInU1`,
  `YieldProcessor`, `MemoryBarrier`, and the constants, as `GCEnv`.
- `env/gcenv.interlocked.h` and `env/gcenv.interlocked.inl`, as `Interlocked`. The
  `InterlockedOperationBarrier` of the C++ version has no counterpart: the
  `System.Threading.Interlocked` operations it forwards to are full barriers on every
  architecture, so the extra fence is already part of them.
- The free functions of `env/volatile.h`, as members of `GCEnv`. The `WithoutBarrier` variants
  use an acquire/release access, which is stronger than the C++ ones and therefore still correct;
  C# has no "not removable but freely reorderable" access.
- `env/gcenv.structs.h`.
- `env/gcenv.sync.h`, less `CLREventStatic`, which the GC does not use.
- The whole declared surface of `GCToOSInterface`, `GCEvent`, `CLRCriticalSection` and
  `AffinitySet` from `env/gcenv.os.h`, with `AffinitySet` implemented rather than forwarded.
- `ParseIndexOrRange` and `ParseGCHeapAffinitizeRanges` of `gcconfig.cpp`, which needed the
  affinity half of `GCToOSInterface`.
- `GCToEEInterface` was already complete in stage 2.
- Layout verification for `GCSystemInfo`, `AffinitySet` and `GCEvent`, and value verification for
  `NUMA_NODE_UNDEFINED`, `MAX_SUPPORTED_HEAPS`, `MAX_SUPPORTED_NODES`, `VirtualReserveFlags`,
  `WAIT_OBJECT_0` and `WAIT_TIMEOUT`, through `GCInterfaceOffsets.h`.
- Virtual memory: `VirtualReserve`, `VirtualRelease`, `VirtualCommit`, `VirtualDecommit`,
  `VirtualReset`, `VirtualReserveAndCommitLargePages`, `GetPageSize`, `GetVirtualMemoryLimit`
  and `GetVirtualMemoryMaxAddress`, from `gc/unix/gcenv.unix.cpp`,
  `gc/windows/gcenv.windows.cpp` and the `GetPageSize` of `env/gcenv.unix.inl` /
  `env/gcenv.windows.inl`. The bodies call `mmap`/`munmap`/`mprotect`/`madvise`/`getrlimit` and
  `VirtualAlloc`/`VirtualFree`/`VirtualAllocExNuma`/`VirtualUnlock`/`GetLargePageMinimum`/
  `GlobalMemoryStatusEx` plus the large page privilege APIs directly, as `[RuntimeImport]`s of
  the libc and Win32 entry points. `GetPageSize` calls `minipal_getpagesize` on Unix, which is
  the same cached `sysconf` value the C++ reads, and returns the fixed 4 KB Windows page size
  otherwise, which is what the C++ `minipal_getpagesize` is there. The `<sys/mman.h>`,
  `<sys/resource.h>` and `<windows.h>` constants are written out per platform in C# and
  asserted against the real headers, for the platform being built, by
  `nativeaot/Runtime/gcenv.managed.cpp`. The old broad `ManagedGC_NUMA_BindMemoryPolicy`
  forwarder is gone; the Unix NUMA bind path now calls narrow state shims directly
  (`ManagedGC_Unix_GetNumaAvailable`, `ManagedGC_Unix_GetHighestNumaNode`,
  `ManagedGC_Unix_BindMemoryPolicy`).
- Write watch: `SupportsWriteWatch`, `ResetWriteWatch` and `GetWriteWatch`, from
  `gc/unix/gcenv.unix.cpp` and `gc/windows/gcenv.windows.cpp`. Windows calls
  `GetSystemInfo`/`GetWriteWatch`/`ResetWriteWatch` directly, as `[RuntimeImport]`s, and keeps
  the C++ shape: feature detection is the same `MEM_WRITE_WATCH` probe reservation of one
  allocation granularity, released again; `GetWriteWatch` passes the same
  `WRITE_WATCH_FLAG_RESET`, treats a zero return as success -- it is an error code, not a
  `BOOL` -- and asserts the reported granularity against `OS_PAGE_SIZE`; the in/out count is
  `ULONG_PTR`, which is `nuint`, so it needs no conversion. `g_SystemInfo.dwAllocationGranularity`
  is read from `GetSystemInfo` at the point of use rather than from a cached global, because the
  managed `Initialize` that fills the C++ one is submodule 3 below; it is the same machine
  constant either way. Unix keeps its exact behavior: a constant `false` that reserves nothing,
  and two methods that only assert. `WRITE_WATCH_FLAG_RESET` and the `SYSTEM_INFO` layout are
  asserted against `<windows.h>` by `nativeaot/Runtime/gcenv.managed.cpp` like the virtual
  memory constants.
- Events and locks: the whole of `GCEvent`, from the `GCEvent::Impl` of `gc/unix/events.cpp`
  and the Win32 `GCEvent::Impl` of `gc/windows/gcenv.windows.cpp`, and the whole of
  `CLRCriticalSection`, from the two halves of `src/native/minipal/mutex.c` that it forwards to.
  The Unix event is the same condition variable, mutex, `m_manualReset` / `m_state` / `m_isValid`
  triple and predicate loop, with the same monotonic deadline -- `pthread_condattr_setclock` plus
  `clock_gettime`, or `clock_gettime_nsec_np` with the relative wait and the spurious-wakeup
  recalculation on Apple -- the same broadcast under the mutex in `Set`, the same auto-reset clear
  in the waiter, and the same `WAIT_OBJECT_0` / `WAIT_TIMEOUT` / `WAIT_FAILED` mapping of the
  `ETIMEDOUT` result. The Windows event is the same handle with `CreateEventW`, `SetEvent`,
  `ResetEvent`, `WaitForSingleObject` and `CloseHandle`, including the two C++ shapes that are
  preserved rather than corrected: `CloseEvent` leaves the Impl and the pimpl pointer behind, so
  `IsValid()` keeps reporting true, and a `CreateEvent` that fails with `NULL` is not recognized
  as a failure by an `IsValid()` that compares against `INVALID_HANDLE_VALUE`. The lock is the
  same recursive mutex: `pthread_mutexattr_settype(PTHREAD_MUTEX_RECURSIVE)` then
  `pthread_mutex_init`, or `InitializeCriticalSection`. All of them are `[RuntimeImport]`s of the
  libc and Win32 entry points, so a `Wait` or an `Enter` parks the thread in libc or the kernel
  with no marshalling and no GC mode transition, exactly as the native GC does while the world is
  suspended. The C++ heap-allocates the Impl with `new (nothrow)`, and the C++ lock embeds its
  `minipal_mutex` by value; the managed versions take both from `ManagedGC_AllocZeroed`, which is
  submodule 2 below, because the managed GC has no allocator of its own yet. The pthread and
  `CRITICAL_SECTION` types are opaque blobs sized above every platform, `struct timespec` is
  written out in the two variants that exist -- two native words, or the 64-bit `time_t` musl
  uses -- and the constants (`PTHREAD_MUTEX_RECURSIVE`, `CLOCK_MONOTONIC`, `CLOCK_UPTIME_RAW`,
  `ETIMEDOUT`) are hardcoded per platform; all of it is asserted against the real headers by
  `nativeaot/Runtime/gcenv.managed.cpp` for the platform being built. `Runtime.ManagedGC` omits
  the Unix `events.cpp`, and `FEATURE_MANAGED_GC` excludes the Windows `GCEvent::Impl` section
  from `gcenv.windows.cpp`, so neither native event implementation is compiled for the managed
  collector. OpenBSD selects its own mmap, rlimit, pthread, clock and errno values rather than
  the Linux defaults.
- Sleep and yield: `GCToOSInterface::Sleep` and `GCToOSInterface::YieldThread`, from
  `gc/unix/gcenv.unix.cpp` and `gc/windows/gcenv.windows.cpp`. `Sleep` is the same early return
  for zero, the same split of the millisecond count into `tv_sec` and `tv_nsec` using the
  `tccSecondsToMilliSeconds` / `tccMilliSecondsToNanoSeconds` conversions of `gc/unix/globals.h`,
  and the same `nanosleep` loop that retries with the remaining interval while the call fails
  with `EINTR`; on Windows it is the same `sleepMSec > 0` guard around a single non-alertable
  `SleepEx`. `YieldThread` is the same single `sched_yield` and assert, or the same single
  `SwitchToThread` whose result is discarded, ignoring `switchCount` exactly as the C++ does.
  `nanosleep`, `sched_yield`, `SleepEx` and `SwitchToThread` are `[RuntimeImport]`s of those
  entry points. `errno` is the one thing that has no C# spelling: it is a macro over a C
  thread-local reachable only through a per-C-library accessor, so the port imports that accessor
  -- `__error` on Apple and FreeBSD, `__errno` on bionic and OpenBSD, and `__errno_location` on
  glibc and musl -- and dereferences it, with the selection and `EINTR` asserted against the
  native platform defines and `<errno.h>` in `nativeaot/Runtime/gcenv.managed.cpp`.
  `FEATURE_MANAGED_GC` excludes both native bodies from `gcenv.unix.cpp` and
  `gcenv.windows.cpp`, so
  `Runtime.ManagedGC` no longer compiles either; the workstation and server archives are
  unchanged.
- Memory limits and cache sizing: `GCToOSInterface::GetPhysicalMemoryLimit`, `GetMemoryStatus`
  and `GetCacheSizePerLogicalCpu`, from `gc/unix/gcenv.unix.cpp` and
  `gc/windows/gcenv.windows.cpp`, together with the helpers under them --
  `GetRestrictedPhysicalMemoryLimit`, `GetPhysicalMemoryUsed`, `GetAvailablePhysicalMemory`,
  `GetAvailablePageFile` and the four `GetLogicalProcessorCacheSizeFrom*` functions on Unix, and
  `GetRestrictedPhysicalMemoryLimit`, `GetLPI` and `GetLogicalProcessorCacheSizeFromOS` on
  Windows. `sysconf`, `getrlimit`, `sysinfo`, `sysctl`, `sysctlbyname`, `sysctlnametomib`,
  `GlobalMemoryStatusEx`, `IsProcessInJob`, `QueryInformationJobObject`,
  `GetLogicalProcessorInformation` and `K32GetProcessMemoryInfo` are `[RuntimeImport]`s of those
  entry points. Every sentinel survives: the `0x7FFFFFFF00000000` above which a cgroup v1 limit
  means "unrestricted", the `SIZE_T_MAX` clamp, the sticky `/proc/meminfo` failure flag, the
  all-`float` load percentage that exceeds 100 over a limit, and the Windows `ullAvailPhys` read
  into `total_physical`. The `_SC_*` names, `struct sysinfo`, `struct xsw_usage`, `struct
  xswdev`, `CTL_VM`/`VM_SWAPUSAGE`, and the Win32 job object, psapi and logical-processor
  layouts are hardcoded per platform and asserted -- with `#error`s on the presence or absence
  of `_SC_AVPHYS_PAGES` and the `_SC_LEVEL*` family -- in `nativeaot/Runtime/gcenv.managed.cpp`.
  `FEATURE_MANAGED_GC` excludes the corresponding sections of `gcenv.unix.cpp`,
  `gcenv.windows.cpp` and `gc/unix/cgroup.cpp`, and the three `ManagedGC_OS_*` forwarders are
  gone. Six narrow Unix leaves stay native because they parse files:
  `ManagedGC_CGroup_GetPhysicalMemoryLimit` and `ManagedGC_Unix_GetPhysicalMemoryUsed` over the
  anonymous-namespace `CGroup` of `cgroup.cpp`, and `ManagedGC_Unix_ReadMemoryValueFromFile`,
  `ManagedGC_Unix_ReadMemAvailable`, `ManagedGC_Unix_GetCurrentVirtualMemorySize` and
  `ManagedGC_Unix_GetProcessAffinitySet` over the `static` `/sys` and `/proc` readers and the
  affinity set of `gcenv.unix.cpp`. They are deleted with submodules 1 and 3 below; Windows
  retains nothing.
- Timers: `QueryPerformanceCounter`, `QueryPerformanceFrequency` and `GetLowPrecisionTimeStamp`,
  from `gc/unix/gcenv.unix.cpp` and `gc/windows/gcenv.windows.cpp`. On Unix each one is a single
  call into `src/native/minipal/time.h`, which is already on every NativeAOT link line, so
  `minipal_hires_ticks`, `minipal_hires_tick_frequency` and `minipal_lowres_ticks` are imported
  directly rather than reimplemented -- `time.c` selects its clock on configure-time probes that
  C# cannot spell. On Windows the C++ calls Win32 directly and so does the port:
  `QueryPerformanceCounter`, `QueryPerformanceFrequency` and `QueryUnbiasedInterruptTime`, each
  asserting on a zero return and then returning the value the failed call left behind rather
  than a sentinel of its own, and the last divided by the same `TicksPerMillisecond` of 10000.
  The output locals carry no initializer, as in the C++; `.locals init`, which this assembly
  does not opt out of, makes the unreachable failure path return zero instead of stack residue. `LARGE_INTEGER` is spelled `long`,
  which `gcenv.managed.cpp` asserts along with the width of `QuadPart` and the existence and
  return width of all six entry points. `FEATURE_MANAGED_GC` excludes the timer sections of
  `gcenv.unix.cpp` and `gcenv.windows.cpp`, the three `ManagedGC_OS_*` forwarders are gone, and
  no native leaf is retained.
- Processor counts and identity: `GetCurrentProcessorNumber`, `CanGetCurrentProcessorNumber`,
  `GetCurrentThreadIdForLogging`, `GetCurrentProcessId`, `GetTotalProcessorCount` and
  `GetMaxProcessorCount`, from `gc/unix/gcenv.unix.cpp` and `gc/windows/gcenv.windows.cpp`. The
  two `HAVE_SCHED_GETCPU` arms are selected by `#if !TARGET_APPLE && !TARGET_FREEBSD &&
  !TARGET_OPENBSD` -- the answer the `gc/unix/configure.cmake` probe gives on every NativeAOT
  Unix target -- and `gcenv.managed.cpp` `static_assert`s the generated `config.gc.h` value
  against that shape on both arms, so the no-`sched_getcpu` platforms keep the C++
  `assert(false); return 0;` and fail closed. Windows translates the `GroupProcNo` packing and
  the `PROCESSOR_NUMBER` layout, both asserted against `<windows.h>`. `GetTotalProcessorCount`
  and `GetMaxProcessorCount` read state that the still-native `Initialize` owns, so rather than
  recompute it with a different lifetime the port reaches it through the narrowest possible
  accessors: `ManagedGC_Unix_GetTotalCpuCount` and the existing
  `ManagedGC_Unix_GetProcessAffinitySet` on Unix, and `ManagedGC_Windows_GetTotalCpuCount` (a
  `uint32_t*`, because the C++ body caches into `g_totalCpuCount` on first call),
  `ManagedGC_Windows_GetSystemInfoProcessorCount`, `ManagedGC_Windows_GetProcessAffinitySet`,
  `ManagedGC_Windows_GetCanEnableGCCPUGroups`, `ManagedGC_Windows_GetCpuGroupCount`,
  `ManagedGC_Windows_GetCpuGroupActiveProcessorCount` and `ManagedGC_Windows_GetCpuGroupBegin`
  on Windows. `ManagedGC_Unix_GetCurrentThreadId` is retained for a different reason:
  `minipal_get_current_thread_id` is a `static inline` over a `_Thread_local` cache rather than a
  linkable symbol, so it goes away when minipal exports a real entry point or when
  System.Private.GC has thread local storage of its own. All processor/affinity/NUMA/CPU-group
  `ManagedGC_OS_*` forwarders are gone; only `ManagedGC_OS_Initialize`,
  `ManagedGC_OS_Shutdown` and `ManagedGC_OS_DebugBreak` remain.
- Focused xUnit coverage of every piece above that is pure computation, in
  `tests/GCEnvironmentTests.cs`; of the whole virtual memory port -- flag translation,
  alignment over-allocation and trimming, failure paths, and a reserve/commit/write/reset/
  decommit/release exercise on raw pages -- in `tests/GCVirtualMemoryTests.cs`; and of the write
  watch port -- the probe and its release, the reset flag, the reported pages and count, the
  granularity, the insufficient-buffer and unwatched-range failures, and the Unix
  reserve-nothing `false` -- in `tests/GCWriteWatchTests.cs`; and of the event and lock ports --
  reset modes, initial states, elapsed timeouts, cross-thread wakeups, one-waiter-per-`Set`
  against all-waiters, a two-event ping-pong that loses no signal, set/reset racing pollers, the
  recursive lock's nesting, contended mutual exclusion, the `CrstHolder` scopes, the recorded
  mutex attribute and condition variable clock, and the injected allocation, mutex and condition
  variable failures -- in `tests/GCEventTests.cs` and `tests/GCCriticalSectionTests.cs`; and of
  the sleep and yield ports -- the zero-interval early return, the second/nanosecond split up to
  `uint.MaxValue`, the `EINTR` retry driven by the remaining interval the previous call reported,
  the absence of a retry for any other `errno`, the ignored `switchCount`, and the Windows
  interval and `bAlertable` forwarding -- in `tests/GCSleepYieldTests.cs`; and of the memory
  limit and cache sizing ports -- restricted and unrestricted limits, the cgroup sentinel and
  read failure, the rlimit and real-memory clamps, the job object limit combinations and the
  address-space check, the load and available-memory calculations including saturation past
  100%, null output pointers, the sticky `/proc/meminfo` failure, the sysfs cache walk, the
  affinity and arm64 CPU-count heuristics at each boundary, and `trueSize` true and false with
  its caching -- in `tests/GCMemoryLimitsTests.cs`; and of the timer ports -- exact forwarding of
  each minipal call once per invocation, the full `int64_t` range including the negative counts
  that check the signed-to-unsigned reinterpretation, and on Windows the same range through the
  `QuadPart` the two `LARGE_INTEGER` calls fill, the truncating division by
  `TicksPerMillisecond` across its boundaries, and the three injected call failures -- in
  `tests/GCTimerTests.cs`; and of the processor/affinity/NUMA/CPU-group ports -- identity
  widening, both `HAVE_SCHED_GETCPU` arms, Windows `GroupProcNo` packing, `GetTotalProcessorCount`
  caching and source selection, `SetThreadAffinity` and ideal-processor branches, thread-priority
  boosting, `SetGCThreadsAffinitySet` set/mask behavior, NUMA and CPU-group info aggregation,
  heap-to-processor mapping with node fallback rules, and affinitize-range entry parsing -- in
  `tests/GCProcessorTests.cs`. All of them run the shipping bodies over recording substitutes for
  their libc and Win32 declarations.

#### Remaining submodules

Each item below is a native module that `nativeaot/Runtime/gcenv.managed.cpp` currently forwards
to. The managed declaration already exists and does not change when the implementation lands;
only the body and its shim do. They are listed in the order they become blocking.

The affinity/NUMA/CPU-group submodule is complete: `SetThreadAffinity`,
`BoostThreadPriority`, `SetCurrentThreadIdealAffinity`, `GetCurrentThreadIdealProc`,
`SetGCThreadsAffinitySet`, `CanEnableGCNumaAware`, `GetNumaInfo`, `CanEnableGCCPUGroups`,
`GetProcessorForHeap`, `GetCPUGroupInfo` and `ParseGCHeapAffinitizeRangesEntry` are translated
for Unix and Windows, and `ManagedGC_NUMA_BindMemoryPolicy` plus
`ManagedGC_Windows_GetCpuGroupProcessorCount` are removed. `ManagedGC_OS_Initialize`,
`ManagedGC_OS_Shutdown` and `ManagedGC_OS_DebugBreak` are the only broad forwarders left in
`gcenv.managed.cpp`. `FEATURE_MANAGED_GC` excludes every one of those C++ bodies -- and the
anonymous-namespace `GetGroupForProcessor` of `gcenv.windows.cpp`, whose only caller went with
them -- except the Windows `CanEnableGCCPUGroups`, which
`nativeaot/Runtime/windows/PalMinWin.cpp` and the retained `GetTotalProcessorCount` in the same
archive still call. The Unix `CanEnableGCCPUGroups` and both `ParseGCHeapAffinitizeRangesEntry`
bodies were retained for `gcconfig.cpp`, and are excluded with the configuration port of stage 4.
The Unix NUMA state is reached through `ManagedGC_Unix_GetNumaAvailable`,
`ManagedGC_Unix_GetHighestNumaNode`, `ManagedGC_Unix_GetNumaNodeNumByCpu` and
`ManagedGC_Unix_BindMemoryPolicy`, the last two because `numasupport.h` declares its functions
with C++ linkage; `ManagedGC_Unix_GetConfiguredCpuCount` is the cpu-set size `SetThreadAffinity`
allocates from.

1. **cgroup and `/proc` file parsing** -- the six `ManagedGC_CGroup_*` / `ManagedGC_Unix_*`
   leaves the memory limit port left behind: the `CGroup` class of `gc/unix/cgroup.cpp` and the
   `static` `/sys` and `/proc` readers of `gc/unix/gcenv.unix.cpp`. They need a
   `read`/`open`-based parser that allocates nothing, so they wait for the GC to have memory of
   its own (submodule 2 below and stage 7).
2. **Heap allocation for the environment** -- `ManagedGC_AllocZeroed` and `ManagedGC_Free`, which
   stand in for the `new (nothrow)` allocations of the environment layer: the `uintptr_t[]` of
   `AffinitySet`, the `GCEvent::Impl` of the event ports, the `minipal_mutex` that the C++
   `CLRCriticalSection` embeds by value, and the `SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]` of the
   Windows `GetLPI`. They can only go away once the GC has memory of its own to take those from,
   which is stage 7.
3. **Initialization** -- `Initialize` and `Shutdown`, plus the shims over state the C++
   `Initialize` fills (`ManagedGC_Unix_GetTotalCpuCount`, `ManagedGC_Unix_GetConfiguredCpuCount`,
   `ManagedGC_Unix_GetProcessAffinitySet`, `ManagedGC_Unix_GetNumaAvailable`,
   `ManagedGC_Unix_GetHighestNumaNode`, `ManagedGC_Windows_GetTotalCpuCount`,
   `ManagedGC_Windows_GetSystemInfoProcessorCount`, `ManagedGC_Windows_GetProcessAffinitySet`,
   `ManagedGC_Windows_GetCanEnableGCNumaAware`, `ManagedGC_Windows_GetNumaNodeCount`,
   `ManagedGC_Windows_GetCanEnableGCCPUGroups`, `ManagedGC_Windows_GetCpuGroupCount`,
   `ManagedGC_Windows_GetCpuGroupActiveProcessorCount`, `ManagedGC_Windows_GetCpuGroupBegin`).
   NativeAOT calls the C++ ones from `PalInit`, so the managed GC never calls these; they land
   last, together with moving that call out of `PalInit`.

`env/gcenv.object.h` is **not** part of this stage after all. NativeAOT overrides it: its own
`nativeaot/Runtime/gcenv.h` supplies `MethodTable`, `Object`, `ObjHeader` and `ArrayBase` from
`Runtime/inc/MethodTable.h` and `Runtime/ObjectLayout.h`, and those -- not the `gcenv.object.h`
definitions -- are what the collector and the EE agree on. The NativeAOT object model is ported
with the core data structures in stage 6.

`nativeaot/Runtime/gcenv.ee.cpp` is likewise not part of this stage: it is the EE's
implementation of `IGCToCLR`, not GC code, and it stays native for as long as the EE does.

Native `gcenv` sources therefore remain in `Runtime.ManagedGC`. Nothing can be removed from that
source list until the modules above are implemented in managed code.

### 4. Standalone GC infrastructure

**Status: In progress**

Translate:

- `gcconfig.h` and `gcconfig.cpp`
- `gcload.cpp`
- `gccommon.cpp`
- `gcscan.cpp`
- `softwarewritewatch.cpp`
- `gcevent_serializers.h` and `gcevents.h`

This stage also brings the first call sites for the `IGCToCLREventSink` vtable translated in
stage 2, through the `FIRE_EVENT` and `KNOWN_EVENT` macros of `gceventstatus.h`.

**Complete when:** configuration, initialization, common helpers, root-scanning infrastructure,
software write watch, and event plumbing no longer depend on placeholder implementations.

#### Done

- `gcconfig.h` and `gcconfig.cpp`, as `GCConfig.cs`. All eighty entries of
  `GC_CONFIGURATION_KEYS` are written out in the table's order, with the same private and public
  keys, the same defaults -- `LARGE_OBJECT_SIZE` and `HEAPVERIFY_NONE` included -- and the same
  widths, a C++ `bool` becoming a `byte` because the EE writes through the pointer the GC hands
  it. Each cached config keeps its `Get{name}()`, `Get{name}(defaultValue)`, `Set{name}(value)`
  and its `s_{name}` / `s_{name}Provided` / `s_Updated{name}` triple; the five string configs are
  read from the EE on every call, as the C++ comment says they are, and handed back in a
  `GCConfigStringHolder` translated as a `ref struct`, so `using` frees the string where the C++
  destructor would, a null string is never freed and a released holder cannot double-free.
  `Initialize`, `RefreshHeapHardLimitSettings` and `EnumerateConfigurationValues` are the same
  three walks of the same table, and `ParseGCHeapAffinitizeRanges` -- ported earlier for the
  affinity work -- keeps every branch of the C++, including the empty list it accepts and the
  range list it ignores when an affinity mask was given too. `HeapVerifyFlags` and
  `WriteBarrierFlavor` are ordinary C# enums; they are deliberately absent from
  `GCInterfaceOffsets.h`, since nothing passes them across the GC/EE boundary.
  `ManagedGCHeap.EnumerateConfigurationValues` now forwards to
  `GCConfig.EnumerateConfigurationValues`, as `GCHeap::EnumerateConfigurationValues` of
  `interface.cpp` does, which is what `RhEnumerateConfigurationValues` and therefore
  `GC.GetConfigurationVariables()` reaches; the smoke test reads the dictionary back and checks
  the reported names, kinds and defaults. It is the first `IGCHeap` body that calls a callback
  parameter, and it calls it through a `delegate* unmanaged[SuppressGCTransition]` view of the
  pointer, because the EE reaches these methods without a reverse P/Invoke frame: a transition
  inside one of them would clear the EE's own transition frame on return and leave the thread
  reporting cooperative mode with an unwalkable stack. Every later body that invokes a callback
  parameter directly has to do the same; callbacks passed back to the EE stay managed pointers
  until their vtable boundary, as `GcScanRoots` does below. `FEATURE_MANAGED_GC` excludes
  `EnumerateConfigurationValues`, `RefreshHeapHardLimitSettings`, `ParseIndexOrRange` and
  `ParseGCHeapAffinitizeRanges` from the C++ `gcconfig.cpp`, which leaves the
  `GCToOSInterface::ParseGCHeapAffinitizeRangesEntry` of both platforms, the Unix
  `CanEnableGCCPUGroups` and both `GetMaxProcessorCount` bodies without a native caller, so those
  are excluded as well. The storage, the accessors and `GCConfig::Initialize` stay compiled:
  `PalInit` calls `Initialize` and the still-native Windows `GCToOSInterface::Initialize` reads
  `GCNumaAware` and `GCCpuGroup` back out of it. They go with the initialization submodule of
  plan step 3 above, which is the same reason the processor and NUMA state accessors are still
  there.
- Focused xUnit coverage in `tests/GCConfigTests.cs`, over a substituted `GCToEEInterface` --
  the first test host that stands in for the EE rather than for libc, because the shipping
  methods are indirect calls through the `IGCToCLR` vtable and no test process has one. Half of
  it is driven by `gcconfig.h` and `gcconfig.cpp`, embedded in the test assembly the way
  `GCInterfaceOffsets.h` is, so every config is checked for its accessors, field types, default
  and declaration order, and the recorded key sequences of `Initialize`,
  `RefreshHeapHardLimitSettings` and `EnumerateConfigurationValues` are compared against the same
  table entry by entry. The other half is behavior: provided versus unprovided precedence, the
  private key winning over the public one, a `NULL` public key never reaching the public
  settings, the full `int64` range and the narrowing of a boolean, the value a "not provided"
  answer still leaves behind, the reported copy that `Set` moves and `Get` does not, the string
  lifetime through the callback and the holder, and the affinitize-range parser's mask, range,
  CPU-group, malformed and out-of-range cases. The enumeration callback is the address of an
  ordinary managed static rather than an `[UnmanagedCallersOnly]` method, because the port calls
  it without a transition and a reverse P/Invoke prologue rejects an already-cooperative caller;
  those ten tests are conditioned on the one architecture where the managed and native calling
  conventions differ.
- The NativeAOT-relevant surface of `gcload.cpp`, as
  `ManagedGCEntryPoints.ManagedGC_VersionInfo` and
  `ManagedGCEntryPoints.ManagedGC_Initialize`. The C# bodies now keep the C++ ordering and
  failure semantics that are reachable in `Runtime.ManagedGC`: clear output pointers first,
  record the incoming `IGCToCLR` pointer before further setup, run interface-layout verification,
  initialize managed `GCConfig`, create the handle manager before the heap, return
  `E_OUTOFMEMORY` on a null creation result. The WKS `USE_REGIONS` path now negotiates DAC 2.8,
  publishes static collector addresses during `GC_Initialize`, completes heap-owned generation
  and allocation addresses after region initialization, and clears them before teardown. Focused tests in
  `tests/ManagedGCEntryPointsTests.cs` verify ABI/version reporting and the null-clr/layout/OOM
  failure paths directly.
- The dependency-closed `GetHighPrecisionTimeStamp` leaf of `gccommon.cpp`, as
  `GCCommon.GetHighPrecisionTimeStamp`. It preserves the same lazily cached
  counter-to-microsecond multiplier and floating-point truncation. Focused tests substitute the
  already-ported performance counter and frequency, pin the scaling at truncation boundaries,
  and verify that the frequency is read only once. `Runtime.ManagedGC` now omits
  `gccommon.cpp`; the globals and helpers needed by later collector modules will be translated
  into `GCCommon.cs` as those consumers arrive.
- `g_gc_lowest_address` and `g_gc_highest_address` of `gccommon.cpp`, as the same-named fields
  of `GCCommon.cs`, in the order they appear there. `GCHeapMemory.Initialize` is what publishes
  them today -- it already computed the same bounds for the card tables -- and its
  `HeapStart`/`HeapEnd` properties now read them back rather than a private field of their own,
  so there is one authoritative place the heap's bounds are set. The rest of `gccommon.cpp`'s
  globals arrive with the collector modules that use them, as before.
- `softwarewritewatch.h` and `softwarewritewatch.cpp`, in full, as `SoftwareWriteWatch.cs`: an
  `internal static unsafe class` with the same `g_gc_sw_ww_table`/`g_gc_sw_ww_enabled_for_gc_heap`
  globals, the same `AddressToTableByteIndexShift` -- read straight out of the generated
  `GCInterfaceOffsets.SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift` rather than restated --
  and the same `WRITE_WATCH_UNIT_SIZE`. Every method the header inlines and every one the source
  file defines out of line is translated in the header's declaration order: table creation and
  resizing (`GetUntranslatedTable(End)`, `(Initialize/Set)UntranslatedTable`,
  `SetResizedUntranslatedTable`, `TranslateTableToExcludeHeapStartAddress`), the
  enable/disable pair (`EnableForGCHeap`/`DisableForGCHeap`, each one `StompWriteBarrier` call
  with `WriteBarrierOp.SwitchTo(Non)WriteWatch`, the table pointer and the suspended flag),
  `StaticClose`, the page/table-index arithmetic (`GetTableByteIndex`, `GetPageAddress`,
  `GetTableByteSize`, `TranslateToTableRegion`), and the dirty-state operations
  (`ClearDirty`/`SetDirty`/`SetDirtyRegion`, `GetDirtyFromBlock`/`GetDirty`, with their exact
  bit-scan-to-page-address arithmetic and the `GCEnv.MemoryBarrierProcessWide` calls the C++
  comments require before and after a dirty scan on an unsuspended runtime).
  the dead `GetTableStartByteOffset` declaration was removed from the native header after
  verifying that it had no definition or caller, so there is no inactive API to mirror.
  `memcpy` and `memset` become
  `Buffer.MemoryCopy` and a small chunked wrapper over `Unsafe.InitBlockUnaligned`, both
  allocation-free CoreLib primitives rather than `NativeMemory`, which owns memory instead of
  merely operating on caller-supplied pointers. The heap bounds `GetHeapStartAddress`/
  `GetHeapEndAddress` read are the two new `GCCommon` globals above.
  `GCEnv.MemoryBarrierProcessWide`, next to the rest of the environment's process-wide
  primitives, is a `[RuntimeImport]` over `minipal_memory_barrier_process_wide`, the same
  process-wide barrier the C++ calls directly rather than through `GCToOSInterface`. Focused
  xUnit coverage in `tests/SoftwareWriteWatchTests.cs` compiles the shipping body directly over
  a synthetic heap of unmanaged memory -- `SoftwareWriteWatch` never dereferences a heap
  address, only shifts it into a table index, so a heap-shaped range of addresses is all a test
  needs -- and a table sized by the port's own `GetTableByteSize`, over a substituted
  `GCToEEInterface.StompWriteBarrier` and a call-counting
  `tests/GCEnv.MemoryBarrierProcessWide.TestHost.cs`. It covers table sizing and alignment, the
  translated table pointer against the raw bytes of the buffer it is translated from,
  `SetResizedUntranslatedTable` preserving dirty bits at their same absolute addresses across a
  resize, `StaticClose`, the exact `WriteBarrierOp`/table pointer/suspended flag of
  `Enable`/`DisableForGCHeap`, page-boundary-exact `ClearDirty`/`SetDirty`/`SetDirtyRegion`, and
  `GetDirty` across a single table block, across several, over an arbitrary subrange, at the
  edge of the caller's output capacity, with dirty state retained versus cleared, with every
  bit-scan position of a table word mapped to its own page, and with the process-wide barrier
  called only when the runtime is not already suspended and only as many times as the C++
  comments say it must be.
- The dependency-closed parts of `gcscan.cpp`, as `GCScan.cs`:
  `GetGcRuntimeStructuresValid`, `GcRuntimeStructuresValid`, and the one-line `GcScanRoots`
  forwarder. The validity counter starts at one, as the C++ global does; a managed-only
  `Initialize` method writes that value during `ManagedGC_Initialize` rather than introducing a
  static constructor. The promote callback remains a managed function pointer inside the
  collector and is representation-cast to the native typedef only inside
  `GCToEEInterface.GcScanRoots`, so ILC does not add a reverse-P/Invoke prologue to callbacks
  invoked by the cooperative-mode EE. Direct tests cover nested invalid regions and verify
  every root-scan argument passed to that wrapper.
- `gcevent_serializers.h`, `gcevents.h`, and the event-firing half of `gceventstatus.h`, as
  `GCEventSerializer.cs` and `GCEvents.cs`. The primitive serializers preserve the native
  sizes, cursor movement, little-endian integral payloads, and raw float representation.
  Because C# has no x-macro facility, all 37 known-event and four dynamic-event enable/fire
  pairs are written out in the order of `gcevents.h`; tests parse that native table and require
  every expanded name to remain present. Known events dispatch through their exact
  `IGCToCLREventSink` vtable slot. The current dynamic rows stack-serialize the generated
  `uint16_t` version followed by every argument from their native `diagnostics.cpp` or
  `dynamic_heap_count.cpp` call site, and pass a null-terminated native event name. The tests
  substitute the EE boundary and verify every payload field and offset, so both suppression by
  provider state and actual known/dynamic dispatch run directly without trying to call a managed
  callback through a `SuppressGCTransition` unmanaged function pointer.

#### Remaining

For `gcload.cpp`, what remains native is outside the managed-GC runtime surface: the C++ file is
still used by CoreCLR and by NativeAOT's workstation/server GC archives, which still need the
native workstation/server heap construction and DAC population paths. `Runtime.ManagedGC` omits
`gcload.cpp` and links `clrgc.managed.cpp` instead, so these native paths are not reachable when
`IlcManagedGC=true`. The rest of `gccommon.cpp` and `gcscan.cpp` is blocked on later heap and
handle-table stages. The disabled `TRACE_GC_EVENT_STATE` debug dump remains with diagnostics
because it requires string-free logging; all current event serializers and expanded event
helpers are translated. `softwarewritewatch.h`/`.cpp` are translated in full; the declaration-only
`GetTableStartByteOffset` was removed from the native header. `Runtime.ManagedGC` actively uses
software write watch for background reset and concurrent/final dirty-page revisit. The remaining
`gccommon.cpp` state is
either compiled out of NativeAOT or belongs to the core heap and region modules in stages 6 and
7; `log_init_error_to_host` also needs the allocation-free native-formatting support used by its
callers. `GCConfig::RefreshHeapHardLimitSettings`
and `GetLOHThreshold` have no managed call site yet because the collector state their C++ callers
-- `gc_heap::refresh_memory_limit` and `init_semi_shared` -- work on does not exist; they arrive
with stages 6 and 7.

### 5. Handle table

**Status: In progress**

Mechanically translate:

- `handletable.cpp`
- `handletablecore.cpp`
- `handletablecache.cpp`
- `handletablescan.cpp`
- `objecthandle.cpp`
- `gchandletable.cpp`

The original flat bootstrap table has been replaced by the translated table. The current runtime
uses one global table until the multi-heap collector state exists. `dac_handle_table` and
`dac_handle_table_segment` of `gcinterface.dac.h` belong here too: their array fields are sized
by `handletableconstants.h`.

#### Done

- `handletableconstants.h`, as `HandleTableConstants.cs`, including the target-pointer-sized
  segment arithmetic, little-endian generation mask, block/clump/mask relationships, cache
  bank sizing, rebalance thresholds, and invalid sentinels.
- The load-bearing segment schema from `handletablepriv.h`, as `HandleTableStructs.cs`:
  byte-packed `_TableSegmentHeader`, the exact 64-KiB `TableSegment`, and the naturally aligned
  `HandleTypeCache`. `dac_handle_table_segment` and `dac_handle_table` are translated alongside
  them. Their 32- and 64-bit offsets, sizes, and alignments are pinned in
  `GCInterfaceOffsets.h`, asserted against the native headers during the runtime build, checked
  again by `GCInterfaceLayout` during managed collector startup, and covered directly by xUnit.
- The segment lifecycle and handle-to-segment mapping from `handletablecore.cpp`, as
  `HandleTableCore.cs`: aligned reservation, page-rounded header commit, exact sentinel and free
  chain initialization, owning-table back pointers, release, and address masking. Direct tests
  exercise the real managed `GCToOSInterface` virtual-memory implementation.
- The dependency-free start of the block allocator: byte-sized block lock helpers and
  `SegmentInsertBlockFromFreeListWorker`, including page commitment, free-list removal, circular
  per-type chain insertion, type/hint/tail bookkeeping, and free counts.
- Handle-slot allocation within committed blocks and existing type chains:
  `BlockAllocHandlesInMask`, `BlockAllocHandlesInitial`, `BlockAllocHandles`, and
  `SegmentAllocHandlesFromTypeChain`. The native low-bit lookup table is represented by the
  allocation-free `uint.TrailingZeroCount` intrinsic.
- Empty-block removal through `SegmentRemoveFreeBlocks`, including locked-block deferral,
  order-preserving free-list insertion, hint/tail updates, free-count repair, and recursive
  reclamation of parallel user-data blocks.
- Handle freeing through `BlockFetchUserDataPointer`, `BlockFreeHandlesInMask`,
  `BlockFreeHandles`, and `SegmentFreeHandles`, including sorted-prefix processing across masks
  and blocks, duplicate-free accounting, parallel user-data clearing, and empty-block removal.
- Chain rebuilding and page trimming through `SegmentResortChains`,
  `DoesSegmentNeedsToTrimExcessPages`, and `SegmentTrimExcessPages`, including deferred
  scavenging, hint/tail repair, address-ordered type and free chains, trailing-empty-line
  tracking, and whole-page decommit.
- The load-bearing `HandleTable.rgTypeFlags` prefix, `TypeHasUserData`, and
  `SegmentInsertBlockFromFreeList`, including capacity preflight, parallel internal data-block
  allocation, linkage and locking, and commit-failure cleanup. `SegmentAllocHandlesFromFreeList`
  and `SegmentAllocHandles` extend allocation from existing chains into newly committed blocks.
- The fixed `HandleTable` header and the lifecycle subset of `handletable.cpp`:
  `HndCreateHandleTable`, `HndDestroyHandleTable`, `HndSetHandleTableIndex`, and
  `HndGetHandleTableIndex`. Creation initializes the first segment, table lock, type flags, and
  trailing per-type main caches without managed allocation.
- `handletablecache.cpp`, as `HandleTableCache.cs`: reserve/free bank reads, writes, synchronized
  transfers, quick and full rebalancing, cache-miss locking, quick-cache exchange, and single or
  repeated allocation/free paths. The dependent `QuickSort`, `CompareHandlesByFreeOrder`,
  `HandleQuickFetchUserDataPointer`, `HandleQuickSetUserData`, `TableAllocBulkHandles`, and
  `TableFreeBulkPreparedHandles` routines are translated with it.
- The dependency-closed metadata and accounting entrypoints: `HandleFetchType`,
  `HandleFetchHandleTable`, `TableContainHandle`, `HandleValidateAndFetchUserDataPointer`,
  `HndSetHandleExtraInfo`, `HndCompareExchangeHandleExtraInfo`, `HndGetHandleExtraInfo`,
  `HndGetHandleTable`, and cache-aware `HndCountHandles`.
- Single-handle destruction with known or discovered type, plus `ZeroHandles`,
  `TableFreeBulkUnpreparedHandlesWorker`, and `TableFreeBulkUnpreparedHandles`. The native
  optional large sorting buffer is unmanaged memory; allocation failure falls back to the same
  block-sized stack chunks as C++.
- Generation-aware object publication through `GetConvertedGeneration`,
  `HndWriteBarrierWorker`, `HndAssignHandle`, and `HndCreateHandle`. The translation retains
  volatile clump-age accesses, conservative age-zero race resolution, special treatment for
  dependent/async-pinned handles, set-handle event publication (including the async-pinned
  walk), and extra-info initialization before referent publication.
- The remaining inline assignment operations: `HndAssignHandleGC`,
  `HndInterlockedCompareExchangeHandle`, and `HndFirstAssignHandle`, including their differing
  barrier and event ordering.
- The runtime `ManagedGCHandleManager` now creates and owns one translated handle table, and its
  manager/store vtables route creation, destruction, assignment, metadata, containment, and
  dependent-handle operations through the translated entrypoints. The NativeAOT smoke test also
  exercises dependent handles through `ConditionalWeakTable`.
- The dependency-closed `objecthandle.h`/`objecthandle.cpp` infrastructure: exact
  `HandleTableMap` and `HandleTableBucket` layouts, the public-type flag table, `Ref_Initialize`,
  `Ref_Shutdown`, bucket removal/destruction, containment, and current home-heap/slot selection.
  Allocation failures at each unmanaged allocation boundary are covered directly.
- `HndCountAllHandles`, the retail `HndNotifyGcCycleComplete` shape, and the variable-handle
  type constants plus get/update/compare-exchange helpers.
- The remaining production type passes: async-pinned promotion and EE graph walk, full-GC
  sized-ref promoted-byte accounting, ref-count callback promotion and enumeration,
  variable-strength dispatch, weak-native-COM short-weak behavior, and weak-interior relocation.
  Creation, assignment, compare-exchange, extra-info, unknown-type destruction, relocation,
  aging, and rejuvenation retain the native per-type layouts and ordering.
- `handletablescan.cpp`'s asynchronous queue: native-sized range nodes, per-table single-scan
  state, block locking, table-lock release/reacquisition, segment-order processing, allocation
  failure tolerance, queue reuse, and cleanup. Background handle scans now use this path instead
  of being skipped.
- The active NativeAOT `FEATURE_JAVAMARSHAL` paths: cross-reference scanning, bridge registration,
  SCC callback construction, bridge promotion lists, client notification, and post-processing
  weak-reference nulling. Inactive configurations compile these paths out.
- The remaining live `IGCHandleManager`/`IGCHandleStore` behavior, including ref-counted
  enumeration and store destruction. The three ABI-retained dead slots still assert/return the
  same values as `gchandletable.cpp`.
- The WKS production `IGCHeap` lifecycle/tuning hooks: UOH publication exclusion through
  `PublishObject`, manual-reset GC-completion gating, nested suspension-pending accounting, and
  validated yield-processor scaling consumed by the translated spin loops. Foundation tests
  cover state transitions and the UOH reader/publication handshake; regular and background
  NativeAOT smokes exercise the runtime call paths.

#### Remaining

Checked-build scan statistics, profiler/ETW-only handle walks, verification diagnostics, and
server/multi-heap table distribution remain blocked. The WKS production allocation, store,
special-type, synchronous/asynchronous scan, weak/dependent, relocation, aging, Java bridge, and
ref-count callback paths are translated.

**Complete when:** handle allocation, caching, scanning, weak/dependent semantics, ref-counted
handles, and per-type behavior match the C++ handle table under differential tests.

### 6. Core GC data structures

**Status: In progress**

Translate the schema from `gcpriv.h` and related headers:

- `heap_segment` (done)
- `generation` (done)
- `alloc_list`
- `dynamic_data` (done)
- Mark structures
- Region tables
- Card and brick tables
- `gcrecord.h`
- `gcdesc.h`

#### Completed

- The dependency-free `gc_rand` leaf from `gcinternal.h`, including its exact linear
  congruential sequence, bounded scaling, and adjacent spin/cross-generation/mark-stack
  constants pinned against native builds.
- The `bk` and `sorted_table` storage schema from `gcinternal.h`, including its leading
  old-allocation link, bucket offset, explicit initialization, and maximum-pointer sentinel.
  Allocation-free binary lookup, sorted insertion, containing-interval removal, and clear run
  over caller-provided storage. Allocation-backed creation, 3/2 growth, old-array queuing, and
  reclamation use the managed runtime's unmanaged allocation surface.
- The dependency-free prefix of `gcrecord.h`: `gc_condemn_reason_gen`,
  `gc_condemn_reason_condition`, `gen_to_condemn_tuning`, `gc_generation_data`, and
  `maxgen_size_increase`. The translation preserves the native enum values, OR-based bit packing,
  pointer-sized fields, and layout. Native static assertions verify the public record layouts,
  while direct tests verify the private tuning record.
- The per-heap mechanism records of `gcrecord.h`: expansion and compaction reasons,
  mechanism-bit enums, and `gc_history_per_heap`. The port preserves the high-bit operation
  marker, reason-bit encoding, and contiguous five-generation history layout.
- The global history record of `gcrecord.h` and its `gc_reason` dependency from `gc.h`, including
  global mechanism-bit helpers. The native verifier now includes `gcinternal.h` and selects the
  actual WKS or SVR namespace, so current and future core-schema assertions use the real
  collector headers rather than synthetic declarations.
- The remaining `gcinterface.dac.h` schema: `dac_generation` and `dac_gc_heap`, generated
  mechanically from `dac_generation_fields.h` and `dac_gcheap_fields.h`. Their complete layouts
  are verified against the native classes and by the managed startup verifier.
- The `gcdesc.h` records and descriptor arithmetic: `val_serie_item`, `CGCDescSeries`, and
  `CGCDesc` size, initialization, backward series lookup, and MethodTable lookup. The
  short-object scanner consumes those descriptors with the native series and slot order, and the
  MethodTable-dependent `CGCDesc::GetNumPointers` helper is now translated for mark-stack
  capacity checks.
- The next foreground-marking prerequisites from `mark_phase.cpp` and `gcinternal.h`: native
  `stolen`/`partial` tag predicates and untagging, the active WKS `USE_REGIONS` 16-slot
  `MARK_PHASE_PREFETCH` `mark_queue_t` transitions, MethodTable/array object-size arithmetic,
  resumable normal and repeating `GCDesc` traversal, and overflow-address extrema. Direct tests
  cover tag values, queue fill/rotation, duplicate drain, verify-empty and region-generation
  boundaries, traversal restart order, object-size target-width arithmetic, and overflow bounds.
  The queue's non-region condemned-generation overload remains deferred with its required
  `gc_low`/`gc_high` state; the native prefetch instruction is a performance-only gap because
  there is no cross-platform managed primitive.
- The active WKS `USE_REGIONS` mark-list and promoted-byte prerequisites: `m_boundary` preserves
  its inclusive list end and stopped exhausted cursor, `m_boundary_fullgc` suppresses list writes
  while retaining WKS extrema, and the per-region survived-counter/object-size overloads preserve
  biased-index and unchecked native-size behavior. The WKS global promoted recording/reset stays
  debug-only. The next dependency-closed lifecycle slice makes the WKS
  `PER_HEAP_FIELD_SINGLE_GC` mark state static: `gc_mechanisms.first_init` /
  `init_mechanisms` retain condemned-generation initialization and the debug latency override.
  The active WKS `USE_REGIONS` `sync_promoted_bytes` leaf now transfers both counter spans
  through all chained regions of the condemned generations into their segment fields; focused
  tests cover that transfer, reset, and generation filtering.
  Native `gcpriv.h` enables `FEATURE_LOH_COMPACTION` unconditionally, so the managed port carries
  the minimal default/once and config-forced LOH request state instead of treating it as
  inactive. The common heap initialization path initializes that state and the static queue
  before the region-specific branch. Collection setup verifies its empty invariant, selects the
  full versus partial mark-list end, resets extrema, and assigns and
  clears the two region-counter spans. Focused tests cover settings reset, debug latency and LOH
  compaction behavior, queue reset, full and partial list setup, counter pointer/zeroing, extrema
  sentinels, repeated lifecycle reset, and the non-publishing null/zero-list failure path. The
  native global `g_mark_list` /
  `g_mark_list_piece` backing allocation, size/growth policy, and ownership remain blocked on
  unported planning and multi-heap globals, so setup intentionally accepts caller-owned storage
  and collection entrypoints remain unrouted.
- The active WKS `USE_REGIONS` `mark_object_simple1`, `mark_object_simple`,
  `drain_mark_queue`, `mark_object`, and `mark_through_object` bodies from `mark_phase.cpp`,
  plus `compute_gc_and_ephemeral_range` from `collect.cpp` and its `gcinternal.h`
  `is_in_gc_range`/`is_in_condemned_gc` prerequisites: `mark_object_simple1` preserves local
  `mark_stack_array` byte-slot traversal, small-object fallback capacity checks via
  `CGCDesc::GetNumPointers`, the native `partial_size_th`/`num_partial_refs` split, continuation
  tags, and queued-tail behavior. `mark_object_simple`/`drain_mark_queue` preserve delayed
  root processing through the 16-slot queue, `m_boundary` use even on full collections, promoted
  accounting, transitive draining until `get_next_marked` returns null, and queue-empty
  postconditions. Focused tests cover partial/full boundary accounting, generation filtering,
  continuation/resume over more than 32 pointer-bearing children, transitive cycle traversal,
  overflow extrema propagation, observable queue-tail ordering, WKS range recomputation, and
  wrapper filtering/traversal. The bounded WKS `GCHeap::Promote` callback bridge now follows the
  native range, condemned-region, interior-resolution, pin, and mark ordering through
  `GCScan.GcScanRoots`; its `ScanContext` and `GCCallFlags` ABI stays unchanged. Its minimal
  pinning sets the object-header bit and counts each pin callback, reset by the bounded root
  lifecycle; ETW and GC statistics publication remain deferred. The bounded
  `mark_phase_stack_roots` prefix preserves the direct WKS root order over the owned mark-list
  and computed range: `BeforeGcScanRoots`, `GcScanRoots(promote)`, queue drain,
  `GcScanHandles(promote)`, queue drain, initial dependent fixed-point scanning,
  `AfterGcScanRoots`, short weak clearing, finalization and drain, a dependent fixed-point
  rescan, long weak/dead-dependent clearing, and the single-threaded sync-block weak callback.
  It remains deliberately non-routed from `IGCHeap.GarbageCollect`. The dependency-closed WKS
  overflow-recovery leaves also drain
  the queue, apply the native mark-stack growth cap, rescan captured marked ranges, and recheck
  until stable, with generation-size, total-heap-size, and promoted-byte accounting. The bounded
  WKS `CFinalize` queue closure is directly testable and its F-reachable root scan is wired
  between the EE-root and strong/pinned-handle drains. Classification through
  The remaining finalizer scheduling and full mark phase remain deferred; no helper is routed
  from a collection entrypoint or `GarbageCollect`.
- The first dependency-free `gcpriv.h` records: `static_data`,
  `recorded_generation_info`, and `etw_opt_info`. These establish the pointer-sized schema used
  by dynamic tuning, recorded GC information, and allocation diagnostics.
- The adjacent recorded-GC and allocation-wait schema: `last_recorded_gc_info`,
  `alloc_wait_reason`, `alloc_thread_wait_data`, and `msl_take_state`, including the contiguous
  five-generation snapshot and one-byte native boolean fields.
- The no-GC-region state and diagnostics schema: `gc_pause_mode`, `no_gc_region_info`, and
  `interesting_data_point`, reusing the translated interface status and finalizer callback types.
- The unconditional core collector enums for LOH compaction, pause-mode results, latency and
  tuning, object-heap identity, memory type, allocation state, and multi-segment-lock entry.
- The unconditional planning and relocation records `plug`, `pair`, `plug_and_pair`,
  `plug_and_reloc`, `plug_and_gap`, `gap_reloc_pair`, `aligned_plug_and_gap`,
  `loh_obj_and_pad`, and `loh_padding_obj`. Their pointer, union, forced-alignment, and padding
  layouts are pinned independently of the later collection-phase algorithms.
- The dependency-free `mark` schema from `gcinternal.h`: all saved plug and relocation pairs,
  the unconditional `SHORT_PLUGS` allocation-context pointer, native four-byte `BOOL` flags, and
  the debug saved post-plug copy. Its short-plug bit helpers preserve their native mask-valued
  `BOOL` results, and its four gap/relocation swaps use direct struct copies. `COLLECTIBLE_CLASS`
  remains gated as it is in `gcpriv.h` (disabled when `FEATURE_NATIVEAOT` is defined).
  allocation-free diagnostics path are translated.
- The adjacent `card_table_info` schema and dependency-free helpers from `gcinternal.h`. The
  DAC prefix remains binary compatible with `dac_card_table_info`; the card-bundle pointer is
  unconditional because `gcpriv.h` always defines `CARD_BUNDLE`, while `mark_array` remains
  conditional on non-WASM `BACKGROUND_GC`. `gib`, brick/card alignment, card-word/bit,
  card-bundle-word/bit, and pointer-to-card arithmetic preserve native unsigned behavior;
  `GC_PAGE_SIZE`, card-word and card-bundle-word widths, target-width card size, card-bundle
  size, card-bundle thresholds, decommit cadence, and the 64-bit memory-load/young-generation
  constants are covered by direct layout and boundary tests.
- The adjacent `gc.cpp` card-table sizing helpers: card words align to and convert through
  card bundles, translated bundle-table pointers retain their zero-based indexing skew, and
  card/brick table sizes cover the same half-open address ranges as native.
- The `card_table_info` pointer accessors from `gcinternal.h` and `translate_card_table` from
  `gc.cpp`: ref-return writes alias the metadata record immediately preceding the card words,
  including conditional background state, and translated tables preserve zero-based indexing.
- The dependency-free background mark-array arithmetic from `gcinternal.h` and `gc.cpp`:
  target-width mark pitch, word layout, alignment, indexing, address reconstruction, and table
  sizing are pinned against native constants and covered at every word boundary.
- The adjacent segment-range traversal helpers from `gcinternal.h`: read-only segments outside
  the current heap range are skipped, address membership retains native half-open bounds, and
  generation iteration starts/stops at the region or segment collector's native indices.
- The dependency-free `alloc_list` core, including the 64-bit `DOUBLY_LINKED_FL` prefix and
  pointer-based ref accessors for the native reference-return API. The shared native size table
  covers both the `TARGET_WASM` exclusion of that prefix and the diagnostic-only
  `FL_VERIFICATION` field; the shipping managed type omits that unused diagnostic field.
- The dependency-free `allocator` core that owns those free lists: the private
  `first_bucket_bits`/`num_buckets`/`first_bucket`/`buckets`/`gen_number` schema, parameterized
  construction and explicit young-generation initialization, `number_of_buckets`,
  `first_suitable_bucket`, `first_bucket_size`, `alloc_list_of`, damage-count lookup, the
  pointer-based head/tail and
  64-bit `added_` ref accessors, `clear`, `discard_if_no_fit_p`, and the non-WASM 64-bit
  `is_doubly_linked_p` predicate. Because every native member is private, the shared table pins
  only the allocator's size and alignment (under the same `FL_VERIFICATION`/`TARGET_WASM`
  combinations as its embedded `alloc_list`) and the managed tests pin field order and accessor
  behavior directly. The native default constructor is represented as a pointer initializer
  because C# does not run struct constructors for embedded fields or unmanaged storage.
- The per-generation `dynamic_data` schema and its dependency-free `dd_*` accessors. The class
  has no native constructor, so zero initialization matches the native default, and its fields are
  public, so the shared table pins every field's offset explicitly. `padding_size` is always
  present because `SHORT_PLUGS` is defined unconditionally; `num_npinned_plugs` and the one-pointer
  shift of every field after it are gated on `RESPECT_LARGE_ALIGNMENT || FEATURE_STRUCTALIGN`,
  which reduces to the GC's `FEATURE_64BIT_ALIGNMENT` (`TARGET_ARM || TARGET_WASM`) because
  `FEATURE_STRUCTALIGN` is never defined here. The direct-field, `sdata`-forwarding, and
  `dd_v_fragmentation_burden_limit` accessors are static ref-returning helpers taking a
  `dynamic_data*`, mirroring the native reference-return API without a managed reference to
  collector state.
- The `FEATURE_EVENT_TRACE` `etw_bucket_info` record and its field-replacing `set` helper.
- The per-heap `generation` schema and its dependency-free accessor block, built on the
  `allocator` and `dynamic_data` cores. Its embedded `allocation_context` is an `alloc_context`,
  which derives from `gc_alloc_context` and adds no fields, so the port reuses the existing
  `gc_alloc_context` layout for it rather than introducing a distinct type. The dependency-closed
  `heap_segment` schema and region-only `generation_region_info` are translated too, including
  region/non-region tails, server-only heap/decommit branches, debug non-region saved fields,
  one-byte native booleans, flag and region-age constants, `init_free_list`, and every
  dependency-free accessor/predicate. `gc_heap` remains opaque because this slice only needs its
  pointer. `region_free_list` now has its native bookkeeping layout and the dependency-closed
  list-management core from `region_free_list.cpp` (reset/add/unlink/transfer/aging/sort and
  region-size accounting over `heap_segment`). A minimal `region_allocator` prefix from
  `gcpriv.h` is also present through `region_alignment` and `large_region_alignment`, with
  `gc_heap.global_region_allocator` carrying the active values. The same slice includes
  `LARGE_REGION_FACTOR`, `region_alloc_free_bit`, `allocate_direction`, and the dependency-closed
  alignment/bit-decoding helpers (`align_region_up`, `align_region_down`, `is_region_aligned`,
  `is_unit_memory_free`, `get_num_units`) from `gcpriv.h`. That unlocks `region_free_list`'s
  deferred `get_region_kind`, dispatch
  wrappers (`add_region`, `add_region_descending`, `is_on_free_list`), and
  `unlink_smallest_region` with its native large-region assertion and early-break flow.
  `heap_segment.thread_free_obj` now directly mirrors the native region-sweep helper: gaps at
  least `min_free_list` (twice `min_obj_size`) append to the segment free list and accumulate
  `free_list_size`; smaller gaps accumulate `free_obj_size`. It only threads caller-provided free
  object storage and performs no allocation. The schema forks on
  `USE_REGIONS` -- the region layout
  replaces `allocation_start`/`plan_allocation_start`(`_size`) with `tail_region`/`tail_ro_region`
  -- which reduces from gcpriv.h's `HOST_64BIT && (!HOST_APPLE || HOST_OSX)` to 64-bit AND not an
  Apple mobile platform for this integrated port, computed once as a build symbol; the two trailing
  gen2 fields are gated on `DOUBLY_LINKED_FL` (`TARGET_64BIT && !TARGET_WASM`), and the
  never-defined diagnostic `FREE_USAGE_STATS` fields are omitted. The class has no native
  constructor, so zero initialization matches the native default for every field except the
  embedded `free_list_allocator`, whose one-bucket young-generation default the native allocator
  constructor supplies; `generation.initialize` reproduces that explicitly because C# does not run
  struct constructors for embedded or unmanaged storage. The `generation_*` accessors -- including
  `generation_alloc_context`, the allocation-context pointer/limit, the free-list and region
  pointers, and `generation_total_plan_allocated` -- are static helpers taking a `generation*`,
  mirroring the native reference-return API without a managed reference to collector state.
- The adjacent `seg_mapping` record and `ro_in_entry` low-bit constant. Under `USE_REGIONS` the
  mapping embeds the complete `heap_segment`; without regions it preserves the boundary,
  `MULTIPLE_HEAPS`-only `h0`/`h1`, and `seg0`/`seg1` fields, including the read-only `seg1`
  tagging convention. Region builds now also carry the global skewed `seg_mapping_table` pointer,
  the explicit WKS initialization-only `gc_heap.min_segment_size_shr` state, and the direct
  `gcinternal.h` mapping helpers needed by `region_allocator::move_highest_free_regions`:
  `seg_mapping_word_of`, `get_region_info`, `get_region_info_for_address`,
  `get_skewed_basic_region_index_for_address`, `get_basic_region_index_for_address`, and
  `is_free_region`. The large-region continuation path preserves the native negative
  `allocated` first-field sentinel and pointer reinterpretation of a `seg_mapping` entry as a
  `heap_segment`. Configured region sizes also receive the native maximum-size and power-of-two
  validation before initializing the shift; adaptive default sizing and range-per-heap validation
  remain deferred. Non-region table-size and address-to-segment/heap algorithms are intentionally
  deferred to `regions_segments.cpp` and `gc.cpp`, after their heap constants and state arrive.
- The region write-barrier publication core from `gc.cpp` and `gcinternal.h`:
  `region_write_barrier_settings`, `stomp_write_barrier_ephemeral`, and
  `set_region_gen_num`. The translated global lock is explicitly initialized at managed-GC
  startup with no static constructor, alongside the native empty-range `MAX_PTR`/null sentinels.
  Generation updates fill every basic-region map entry before gen0/gen1 synchronize; an
  expanding update publishes the skewed map and shift through `StompEphemeral` before it changes
  the global ephemeral bounds, while a contended updater rechecks coverage before spinning.

This completes the `gcinterface.dac.h` translation started in stage 2. Publishing live DAC state
still waits for the corresponding collector structures.

**Complete when:** the managed types preserve the required native layouts and remain compatible
with DAC/cDAC descriptors such as `dac_gcheap_fields.h`, `dac_generation_fields.h`, and
`datadescriptor`.

### 7. Memory and region management

**Status: In progress -- `region_allocator::init`, initial-region reservation state, spin-lock enter/leave, endpoint block marking, terminal allocation, free-block search/callback allocation, public region-allocation wrappers, inline public accessors, region deletion, USE_REGIONS mapping helpers, highest-free-region movement, the first `memory.cpp` commit/accounting helpers, WKS `USE_REGIONS` region decommit, region write-barrier publication, and the first `regions_segments.cpp` mapping and region-return lifecycle helpers are translated**

Translate:

- `memory.cpp`
- `region_allocator.cpp`
- `region_free_list.cpp`
- `regions_segments.cpp`

Done so far:

- `region_free_list.cpp`: the dependency-closed list-management core (`verify`, `reset`,
  add/unlink front, descending insertion, transfer, age increment, and committed-size/age merge
  sort), plus the adjacent `get_region_start` / `get_region_size` /
  `get_region_committed_size` helpers from `gcinternal.h` that those methods depend on.
- `region_allocator.cpp/gcpriv.h` alignment/configuration prerequisite slice: the exact native
  field prefix through `region_alignment` / `large_region_alignment`, their getters,
  `LARGE_REGION_FACTOR`, `region_alloc_free_bit`, `allocate_direction`, the dependency-closed
  alignment/bit-decoding helpers, and `gc_heap.global_region_allocator` as the managed state
  carrier.
- `region_allocator.cpp/gcpriv.h` schema-extension prerequisite slice: the exact native field
  order through `GCSpinLock region_allocator_lock`, the four `region_map_*` pointers, and the two
  used-free-unit counters, plus the minimal dependency-closed `GCSpinLock` schema and lock-free
  constructor-sentinel initialization helper needed to carry that field.
- `region_allocator.cpp` spin-lock enter/leave behavior: the native compare-exchange acquire
  loop, inner volatile-read spin with `YieldProcessor`, `-1` free / `0` held encoding, debug
  `holding_thread` sentinel/current-thread updates, and release-store publication of the free
  state.
- Dependency-closed map arithmetic from `region_allocator.cpp`: `region_address_of` and
  `region_map_index_of`.
- Dependency-closed map initialization from `region_allocator.cpp`: `region_allocator::init`
  aligns the reserved range, initializes the allocator bounds and free-unit counters in native
  order, allocates the zeroed `uint32_t` map through `ManagedGC_AllocZeroed`, preserves the
  lowest/highest output-pointer success semantics, fails without calling the shim when the map
  byte count overflows `size_t`, and leaves the native allocation-failure logging string deferred
  until string-free GC init logging is ported.
- The reservation state immediately after `region_allocator::init` from `init.cpp`:
  `initial_regions` is the same unmanaged `[heap][generation][start/end]` table, and
  `allocate_initial_regions` preserves the native POH-large, gen2-to-gen0-basic, then
  LOH-large forward-allocation order. Its entries retain the allocator's start/end outputs;
  the null callbacks deliberately leave bookkeeping coverage unchanged. The producer's
  allocation failure returns false rather than relying on the native assertion. The staged
  startup owner frees this table on rollback or shutdown; the full `interface.cpp` lifecycle
  remains deferred.
- Dependency-closed endpoint map updates from `region_allocator.cpp`: `make_busy_block`,
  `make_free_block`, and `allocate_end` preserve the native forward/backward boundary tests,
  endpoint-only busy/free encoding, high-bit free marker, unsigned size arithmetic, exact-fit
  behavior, and the fact that free-unit counters are updated by callers rather than by these
  helpers. The native `dprintf(REGIONS_LOG)` debug traces and
  `ASSERT_HOLDING_SPIN_LOCK(&region_allocator_lock)` checks remain explicitly deferred until the
  managed GC has string-free region logging and spin-lock ownership diagnostics.
- Dependency-closed region deletion from `region_allocator.cpp`: `delete_region` keeps the
  native lock-wrapper shape, while `delete_region_impl` preserves aligned-region assumptions,
  busy/free endpoint decoding, left/right used-free-unit counter updates, previous/next
  free-block coalescing, left/right terminal contraction, map-pointer movement, the native
  `int free_block_size` conversion through pointer arithmetic, and `total_free_units` mutation
  after the map/counter updates. The native region `dprintf` traces, `print_map`, and
  `ASSERT_HOLDING_SPIN_LOCK(&region_allocator_lock)` diagnostics remain deferred.
- Free-block search and callback allocation from the private `region_allocator::allocate`:
  the managed port keeps the exact enter/leave lock lifetime, direction-based current/end
  index selection, used-free-unit fast gate, backward `current_index - 1` endpoint read,
  busy-block skipping, fit/split placement for exact and oversized free blocks, left/right
  used-free counter ownership, terminal `allocate_end` fallback, callback invocation on
  `global_region_left_used`, `total_free_units` mutation order, and callback-failure rollback
  through `delete_region_impl`. The callback typedef is a managed static function pointer
  returning byte (`delegate*<byte*, byte>`), matching the GC vtable convention for callbacks
  implemented by this assembly without allocating delegates or creating reverse P/Invoke thunks;
  native `bool` is represented by the explicit one-byte result.
- Public region-allocation wrappers from `region_allocator.cpp`: `allocate_region`,
  `allocate_basic_region`, and `allocate_large_region` preserve basic-region alignment, large
  default sizing, power-of-two large-size rounding, `uint32_t` unit truncation, forward/backward
  direction forwarding, `start`/`end` writes even after failed allocation, generation-to-ETW
  segment type selection through the `gc.h` constants, and the native `GCCreateSegment_V1`
  event call including its failed-allocation pointer arithmetic.
- Inline public region-allocation accessors from `gcpriv.h`: `get_va_memory_load`, `get_free`,
  `get_used_region_count`, `get_start`, and `get_left_used_unsafe` preserve pointer-difference
  percentage arithmetic with `uint32_t` truncation, target-width free-byte overflow, the right-map
  unused debug assertion, and raw pointer returns.
- Remaining `region_free_list.cpp` helpers previously blocked on that prerequisite:
  `get_region_kind`, `add_region`, `add_region_descending`, `is_on_free_list`, and
  `unlink_smallest_region` with native large-region assertion and early-break behavior.
- Highest-free-region movement from `region_allocator.cpp`:
  `move_highest_free_regions` preserves the caller-locking contract, descending left-map endpoint
  traversal, busy-map and small/large filters, `gc_heap` region-info lookup/free checks,
  destination-list exclusion, source unlink before destination add, and signed quota break.
- The dependency-closed opening helpers of `memory.cpp`: `virtual_alloc_commit_for_heap`,
  `virtual_commit`, `reduce_committed_bytes`, `virtual_decommit`, and `virtual_free`. The slice
  mechanically adds the minimal accounting state those functions touch: the
  `recorded_committed_*` bucket constants, `committed_by_oh`, `current_total_committed`,
  `current_total_committed_bookkeeping`, `heap_hard_limit`, `heap_hard_limit_oh`,
  `check_commit_cs`, `reserved_memory`, and `never_decommit_p`. It preserves the hard-limit
  decision and output flag, no-OS-call exceeded path, OS commit rollback, bookkeeping
  accounting, the large-page/never-decommit heap-memory bypass, decommit-success gating, and
  release-success-only reserved-memory subtraction. `ManagedGCHeap.Initialize` explicitly
  initializes `check_commit_cs` before heap memory initialization.
- WKS `USE_REGIONS` region decommit from `memory.cpp`: `decommit_region` and `decommit_step`,
  plus the real prerequisites they require (`settings.pause_mode`,
  `global_regions_to_decommit`, page alignment helpers, shared `memset`/`memclr`, the
  `DECOMMIT_SIZE_PER_MILLISECOND` cadence, and the background-GC mark-array decommit slice
  used by region cleanup). This preserves GCFreeSegment event timing, exact page-aligned
  decommit ranges, never-decommit direct accounting, failed-decommit and never-decommit memory
  clearing extents, used/committed state updates, mark-array committed-flag cleanup and
  accounting, allocator deletion, and per-step quota/free-list early return.
- The dependency-closed WKS `USE_REGIONS` opening helpers from `regions_segments.cpp`:
  `align_on_segment`, `ro_seg_begin_index`, `ro_seg_end_index`,
  `size_seg_mapping_table_of`, `size_region_to_generation_table_of`,
  `seg_mapping_table_add_ro_segment`, and the intentionally empty
  `seg_mapping_table_remove_ro_segment`. This preserves absolute basic-region indices, lower/up
  segment alignment, lowest/highest-address clipping, the embedded-`heap_segment`
  reinterpretation of `seg_mapping`, and the native `ro_in_entry` sentinel in
  `heap_segment_allocated`.
- The next dependency-closed WKS `USE_REGIONS` lifecycle/accounting slice from
  `regions_segments.cpp`: `clear_region_info` and `return_free_region`, plus the small
  prerequisite helpers they directly require (`clear_brick_table`, `clear_cards`,
  `clear_card_for_addresses`, background changed-segment recording, and debug
  mark-array range verification). This preserves SOH brick clearing vs UOH brick skipping,
  card clearing over the basic-region range, BACKGROUND_GC changed-segment recording,
  committed-byte transfer from the owning object heap to the free bucket under
  `check_commit_cs`, descending free-list dispatch through `region_free_list`, and clearing
  each basic region's `allocated`/continuation sentinel without resetting its generation
  diagnostics.
- The adjacent dependency-closed WKS `USE_REGIONS` lookup slice from
  `regions_segments.cpp`: `region_of`, `get_region_at_index`, and
  `get_region_gen_num(heap_segment*)`, plus the region-only start-object helpers
  `get_uoh_start_object`, `get_soh_start_object`, and `get_soh_start_obj_len`. `region_of`
  preserves direct absolute-address indexing of the already-skewed mapping table;
  `get_region_at_index` adds the shifted lowest heap address before indexing; and direct
  generation lookup reads the embedded segment field. The start-object helpers preserve the
  region-build results (region memory and zero SOH start length).
- The narrow WKS `card_table.cpp` `clear_gen0_bricks` leaf and `gcinternal.h`
  `get_brick_entry` read: these preserve Gen0 region-chain traversal, the native half-open aligned
  brick clearing range, one-time clearing, and direct table reads. The adjacent
  `fix_brick_to_highest`, `find_first_object`, and `gc.cpp` `find_object` leaves preserve the
  object-size/alignment walk, positive brick offset and negative back-link encoding, lazy SOH
  repair, and zero-brick UOH linear lookup. The matching allocation producer and foreground
  mark-phase decay retain the native seven-collection fast-find lifetime. Interior
  promotion/pinning uses those lookup leaves through the direct WKS `GCHeap::Promote` callback;
  collection routing remains deferred.
- The region-generation-map read/flag slice from `gcpriv.h` and `regions_segments.cpp`:
  byte-sized `region_info`, including current and planned generation fields plus `RI_SIP` and
  `RI_DEMOTED`; the absolute and absolute-address-skewed map pointers; object-address generation,
  plan-generation, and demotion reads; and sweep/demotion flag updates. The read path preserves
  native skewed indexing, while flag updates use the unskewed index and retain the unrelated
  packed bits.
- The synchronization-sensitive write-barrier slice: the `gc.cpp` region-flavor settings and
  `StompEphemeral` call, plus `gcinternal.h` `set_region_gen_num`. The explicit startup path
  initializes the global lock and `MAX_PTR`/null collector bounds without a managed static
  constructor. Gen0/gen1 updates fill the basic-region map, acquire the native sentinel lock
  only when the current range does not cover the region, recheck coverage after contention,
  stomp before publishing expanded bounds, and release through the GC volatile helper.
- The dependency-closed WKS `USE_REGIONS` `init_heap_segment` from
  `regions_segments.cpp`. It retains only `heap_segment_flags_ma_committed` when reusing a
  region during background GC, resets the native allocation and background fields, publishes the
  clamped generation through `set_region_gen_num`, and initializes each large-region continuation
  with its negative backtracking sentinel and generation fields.
- The dependency-closed WKS `USE_REGIONS` `init_table_for_region` from `plan_phase.cpp`, its
  `gc.cpp` generation-to-object-heap mapping, and the required `background.cpp`/
  `diagnostics.cpp` mark-array helpers. The port preserves inclusive saved-background-range
  tests, page-rounded mark-array commitment, accounting rollback on failed commits, the
  secondary card-table mark-array path, committed and partial-commit flags, debug clear checks,
  failure-driven region decommit, and SOH-only first-brick initialization. Region partial mark
  commitments remain asserted as they are natively.
- The dependency-closed WKS `USE_REGIONS` `make_heap_segment` and
  `allocate_new_region` paths from `regions_segments.cpp`. They retain basic versus
  large/huge allocator selection and rounding, first-page commitment and accounting, region-map
  segment publication, generation initialization, and allocator rollback after a failed commit.
  The adjacent `reset_heap_segment_pages`, `decommit_heap_segment_pages`, and worker preserve
  their page-up reset boundary, decommit threshold, retained-space, never-decommit,
  object-heap-accounting, and committed/used-clamping behavior.
  The matching `card_table.cpp` bookkeeping slice is now dependency-closed: its native element
  enum/layout, card/brick/region-generation/segment-mapping/mark-array reservation, initial
  and incremental commit-range calculation, page rounding and element-bound clamping,
  bookkeeping accounting, partial-commit rollback, old/new size tracking, coverage doubling and
  minimum-range retry are translated. `on_used_changed` therefore refuses growth when no
  bookkeeping table is installed instead of returning bootstrap success. The current WKS
  configuration has no software-write-watch table and does not enable card bundles, so their
  optional native allocation branches remain deferred; card-bundle metadata is still laid out
  and initialized. The translated card and dependent table pointers are installed before
  publishing `card_table`; the write-barrier stomp itself remains owned by the deferred full heap
  initialization path.
- The dependency-closed WKS `USE_REGIONS` `get_free_region` path from
  `regions_segments.cpp`. It preserves local basic/large selection, local smallest-fitting huge
  selection, caller-held-GC-spin-lock validation before the global huge fallback, reused-region
  initialization with `existing_region_p`, free-to-object-heap committed-accounting transfer
  under `check_commit_cs`, allocation fallback, and `init_table_for_region` null failure
  semantics. The explicit GC spin-lock leaf has the native compare-exchange acquire loop,
  debug owner sentinel, and volatile release store; startup initializes it without a static
  constructor. The WKS project does not build `MULTIPLE_HEAPS`, so server per-heap accounting
  diagnostics and cross-heap selection remain deferred. LOH/POH flags stay at their native
  deferred generation-threading callers rather than being set by this path.
- The next dependency-closed WKS construction/threading slice: `generation_of` and
  `make_generation` from `init.cpp`, `heap_segment_rw` / `heap_segment_next_rw` and
  `thread_uoh_segment` from `gcpriv.h` / `regions_segments.cpp`, and `get_new_region` from
  `plan_phase.cpp`. The raw contiguous generation-table parameter represents the still-deferred
  `gc_heap.generation_table` field without inventing a partial heap layout. Construction retains
  the initialized allocator shape while clearing all native allocation/free-list state, assigns
  generation numbers and start/allocation/tail segment fields, and preserves the WKS
  `DOUBLY_LINKED_FL` reset. Threading skips read-only segments and retains native append order.
  `get_new_region` assigns LOH/POH flags at their native owner before linking and publishing the
  UOH tail, and retains the null failure path without changing the generation list. The WKS
  initial SOH/UOH constructors now consume the reserved initial regions through the same
  raw generation-table adapter, preserving gen2-to-gen0 construction order, commit-failure
  short-circuiting, gen0 ephemeral publications, and LOH/POH flags. The allocation-owned WKS
  `gc_heap` allocation state now holds its unmanaged generation table, dynamic-data table, ephemeral
  segment, allocation counters, allocation quantum, heap number, and SOH/UOH more-space locks.
  `ManagedGCRegionBootstrap` now calls this sequence from production
  `IGCHeap::Initialize`, reserves the configured/default range, creates and extends
  bookkeeping coverage, and releases the range, maps, table and generation storage on every
  failure or shutdown. Production SOH/UOH allocation-context refills now create a
  `try_allocate_more_space_context` over that heap and run its concrete managed lock/budget
  callback; formatted-tail retirement preserves the native allocation counters. The explicit
  non-collecting bootstrap budget policy may consume already-reserved initial regions after the
  native budget is depleted, but it does not claim a collection. UOH retry and collection remain
  deferred, so an exhausted initial UOH region returns null.
- Still deferred from `memory.cpp`: `decommit_ephemeral_segment_pages` and
  `decommit_ephemeral_segment_pages_step`, because they pull in ephemeral generations,
  region/segment decommit targets, and the server-GC decommit-step branch.
- Still deferred from `regions_segments.cpp`: initial memory reservation/destruction,
  mutable read-only segment list operations, full heap-segment deletion, and free-region
  distribution.
  The dependent allocation helpers remain blocked on region allocation and generation
  construction.

**Complete when:** reservation, commitment, release, region allocation, free lists, and segment
lifecycle match the C++ collector.

### 8. Allocator and write-barrier interaction

**Status: Production region allocation and synchronous foreground exhaustion retry routed**

Translate `allocation.cpp`, including:

- Allocation contexts
- Free lists
- Allocation budgets
- `allocate_more_space`
- Large, pinned, and small object paths
- `card_table.cpp` interaction

The first dependency-closed WKS `USE_REGIONS` leaves are in `GCAllocation.cs`: `Align`,
`get_alignment_constant`, `a_size_fit_p`, `void_allocation`, and the pointer/limit reset path
of `fix_allocation_context`; `make_unused_array`/`make_free_obj` and the `CObjectHeader::SetFree`
memory writes they require; and `new_allocation_limit`/`limit_from_size` plus the
allocation-context refill transition of `adjust_limit_clr`: the discontinuous-hole free object,
the region-only null-context and contiguous-gen0 branches, limit/accounting update, ephemeral
used-boundary publication, native right-edge clear-range selection, zeroing-optional syncblock
clearing/object skipping, and the used-endpoint publication for a partially unused span. The
selected more-space lock is released through the unmanaged callback before either potentially
expensive clear, and the allocation wrapper observes the released ownership so it does not
release the lock twice. BGC mark-bit tracking, allocation-info/event emission, brick updates,
and verification stay explicit deferrals; this leaf does not claim that those collector-owned
branches ran. `grow_heap_segment` and the non-background,
non-LOH-compaction/verification portion of `a_fit_segment_end_p` now select committed or
reserved segment-end space, choose `limit_from_size`, commit up to the native 16-page minimum
through `virtual_commit`, preserve its hard-limit result, advance the selected SOH/UOH pointer,
and hand the range to `adjust_limit_clr`. `uoh_a_fit_segment_end_p` walks writable UOH segments,
reports a failed commit as `oom_cant_commit`, restores the UOH allocation-context limit, and
updates `generation_end_seg_allocated`. They preserve the native free-object method table,
array-length, free-list marker, unsigned arithmetic, and accounting updates. The heap-owned
dynamic-data table, allocation quantum, generation table, selected SOH/UOH total,
`alloc_allocated`, ephemeral segment, and heap number remain explicit unsafe inputs until the
refill caller and heap state are translated. The next WKS free-list slice adds
`unused_array_size`, allocator front insertion and unlinking (including native undo bookkeeping),
`thread_free_item_front`, `a_fit_free_list_p`, and `a_fit_free_list_uoh_p`. The SOH path scans
the native bucket chains, discards no-fit entries only for the single-bucket allocator, splits
only formatable remainders, transfers free-list/free-object accounting, and hands the acquired
range to `adjust_limit_clr`. The UOH path retains its exact-or-formatable fit rule, restores the
allocation-context limit after that handoff, and tracks the UOH free-list allocation and
remainder accounting for both LOH and POH. The thin `soh_try_fit` / `uoh_try_fit` layer now
orchestrates those leaves in native order: SOH tries the free list, applies short-end suppression,
then walks/rolls over ephemeral regions; UOH tries its free list, then walks writable allocation
segments and forwards a commit failure as `oom_cant_commit`. The SOH rollover uses the exact
`fix_allocation_context` subset native calls there (`for_gc_p == true, record_ac_p == false`) and
then publishes the old region's allocation pointer. The dynamic planning result consumed by
`short_on_end_of_seg`, the heap-owned state it would normally read, and the subset's concurrent
verification and allocation-context-statistics paths remain explicit deferrals rather than
plausible replacements. The BGC allocation cookie/tracking and `heap_segment_flags_uoh_delete`
branches, LOH-compaction padding, verification syncblock write, heap-owned free-list routing,
budget policy, `clearp`/`resetp` branches of
`make_unused_array`, clearing, and allocation-info/events remain deferred.
The synchronous full-GC condemned-generation allocation closure is now translated for WKS
`USE_REGIONS`: `size_fit_p`, relocation-aware `grow_heap_segment`, SIP/cross-generation
`get_next_alloc_seg`, pin-promotion policy and attribution, `init_alloc_info`, and
`allocate_in_condemned_generations`. It preserves pinned-queue consumption, planned-limit
clipping, front/tail padding and large-plug suppression, plug-length heuristics, short-tail
conversion to pinned, allocation/pinned/free accounting, generation promotion, segment
growth/transition, and region plan-generation publication. The bounded WKS `USE_REGIONS`
synchronous foreground `plan_phase_synchronous_foreground` adapter and its native-named
dependency closure are translated too: pinned-plug conversion/gap metadata,
`find_next_marked`, saved allocation bounds, remaining-pin/region planning, exact 6-MiB
large-pin demotion and 90-percent SIP thresholds, SIP sweeping, marked/pinned clearing,
relocation flags, brick trees/sentinels, plan-generation publication, compaction policy,
LOH/POH handling, compact/sweep execution, final region bounds, finalizer fill pointers,
handle ages, cards, and full-compaction accounting preserve native order.
Unsupported heap/settings, active BGC, malformed generation/segment/mark-stack state, and
inconsistent mark bounds are rejected before mutation. Partial collections preserve the older
generation allocator snapshot, condemned allocation, promotion/demotion, dirty-card marking and
relocation, compact-or-sweep execution, generation bounds, finalization, handles, and card
clearing. The boundary stops before non-region, server, background, and configuration-driven
diagnostic paths.
`try_allocate_more_space` now has an explicit unmanaged state-machine core over the translated
fit paths. It preserves the SOH/UOH `allocation_state` transitions, generation selection,
allocation flags, commit-failure/short-end/OOM propagation, retry exits, UOH acquisition states,
and the existing budget mutations made by fitting. `create_try_allocate_more_space_context`
now reads the concrete WKS heap-owned fields rather than requiring callers to hand-build those
inputs. Its unmanaged callback enters/leaves the selected SOH/UOH lock and implements WKS
`new_allocation_allowed`, including the gen0 elapsed-time throttle. The production unmanaged callback now owns the WKS SOH/UOH more-space locks, allocation-budget
checks, UOH region acquisition, retry decisions, synchronous foreground triggers, and OOM/null
completion. SOH budget pressure begins with Gen0, out-of-space SOH retry begins with Gen1, and
exhausted older/UOH budgets elevate to Gen2. UOH acquisition preserves the native
more-space-lock release, GC-lock acquisition, intervening-full-GC observation, region sizing and
threading, lock reacquisition, LOH accounting, and retry ordering for both LOH and POH.
Full-GC notification is now translated for WKS. Allocation refills perform the native
pre-budget and exhausted-budget checks, blocking full-compaction retries publish an approach,
and foreground/background Gen2 completion resets the approach event and signals the completion
event with the concurrent-GC `NotApplicable` state. Registration, cancellation, timeout, and
cancel races preserve the native percentage and manual-reset-event protocol. User-thread waits
cross one ordinary P/Invoke for the complete OS wait so the runtime can suspend the waiter
without exposing a held event mutex. The WKS `allocate_more_space`
wrapper retries from the native initial state and clears transient state before re-entry; when a
deferred failure follows a concrete lock acquisition, it releases that lock without discarding
the deferred operation. An allocation that observes a started collection leaves the managed-GC
critical region, waits, and retries in native order. The heap bootstrap now also ports the allocation-owned
`dynamic_tuning.cpp` initialization subset: the literal WKS `static_data_table`, latency-level
selection, write-watch/concurrent capability budget selection, configured/default workstation
segment sizing, cache/physical-memory Gen0 minimum, configured Gen0/Gen1 maxima, and
`set_static_data` / `init_dynamic_data` field initialization.
Every SOH/LOH/POH dynamic record therefore begins with its native minimum budget in
`new_allocation`, `gc_new_allocation`, and `desired_allocation`, plus its static fragmentation
and desired-allocation policy. The pure scalar survival-growth and linear-allocation correction
helpers are translated, along with WKS `collect.cpp` `update_collection_counts`,
`update_end_ngc_time`, and `update_end_gc_time_per_heap`. The latter records one end timestamp
and updates elapsed times only for condemned generations.
The WKS `USE_REGIONS` post-collection policy now retunes those records from survival,
fragmentation, elapsed time, incoming promotion, finalization promotion, memory load, and latency
mode. Initialization and runtime refresh compute explicit, percentage, per-object-heap, and
restricted-container hard limits before those budgets are consumed.
Production allocation uses these records and invokes the bounded collector when their budgets or
available regions are exhausted.
`set_allocation_heap_segment`/`reset_allocation_pointers` cover the region generation schema.
Production SOH and UOH allocation-context refills use the region heap. `GCHeapMemory`
remains for unmanaged frozen-segment metadata; on region targets it does not replace region
bounds or publish bootstrap card tables/write-barrier bounds.

The former 256 MB bump allocator is now a 64 KiB metadata-only range for frozen-segment records.
It can be removed when those records gain their final unmanaged owner.

**Complete when:** allocation behavior, alignment, accounting, failure paths, and write-barrier
state match the C++ implementation across supported architectures.

### 9. Collection phases

**Status: In progress -- the bounded WKS `USE_REGIONS` synchronous foreground Gen0/Gen1/Gen2 lifecycle is routed;
pinned-plug queue enqueue/save/dequeue handoff, mark-stack growth/reset setup,
object-header special-bit and padded-plug prerequisites, short-object descriptor scan, the
foreground `gc_mark1`/`gc_mark` leaves, the active WKS `USE_REGIONS`
`mark_object_simple1`/`mark_object_simple`/`drain_mark_queue`/`mark_object`/
`mark_through_object` bodies, dirty-card marking/relocation, `compute_gc_and_ephemeral_range`,
and the synchronous WKS compaction-policy closure, handle relocation branch, and bounded
`relocate_phase` orchestration are translated**

Translate in dependency order:

- `mark_phase.cpp` (the dependency-closed pinned-plug queue enqueue/save and mark-stack
  growth/reset setup, plus the `CObjectHeader`/`MethodTable` special-bit, pointer-flag,
  short-plug-size, padded-plug, and `go_through_object_nostart` descriptor-scan prerequisites,
  `is_mark_bit_set`/`clear_mark_array`, the active WKS `USE_REGIONS` `gc_mark1`/`gc_mark`
  leaves, and the active WKS `USE_REGIONS` `mark_object_simple1`/`mark_object_simple`/
  `drain_mark_queue`/`mark_object`/`mark_through_object` traversal leaves are translated;
  mark-array indexing preserves the native absolute-address bias and first partial-word clear;
  `gc_mark` preserves the half-open range and region-generation tests; `mark_object_simple1`
  preserves local mark-stack aliasing, partial-object continuation, and queued-tail semantics;
  `mark_object_simple`/`drain_mark_queue` preserve delayed root queueing, `m_boundary` usage
  during full collections, and queue-empty draining semantics; the WKS `gc_low`/`gc_high`
  state, `compute_gc_and_ephemeral_range`, `is_in_gc_range`, and `is_in_condemned_gc` enable
  the two wrappers without routing them; the direct WKS `GCHeap::Promote` bridge invokes those
  leaves through `GCScan.GcScanRoots` with native range, condemned-region, interior-resolution,
  pin, and mark ordering. It sets the object-header pinned bit and counts each pin callback,
  resetting that counter at the bounded root lifecycle; ETW and GC statistics publication remain
  deferred. The adjacent WKS `GCHeap::Relocate` bridge and `is_in_find_object_range` preserve the
  bookkeeping-covered gate, condemned-range rejection, exact SOH relocation, and compacting-LOH
  interior offset. The bounded synchronous `GcScanHandles(relocate)` branch now routes
  that callback through sync-block weak pointers, the native physical-order multi-type
  weak/strong/ref-counted/sized-ref handle scan, pinned handles, dependent primary/secondary
  slots, and weak-interior delta adjustment, with native age-mask filtering for Gen0/Gen1.
  Concurrent scans remain deliberately unrouted. The bounded
  `mark_phase_stack_roots` lifecycle preserves the direct WKS root order over the owned mark-list
  and computed range: `BeforeGcScanRoots`, stack-root scanning, queue drain, finalizer-root
  scanning, queue drain, `GcScanHandles(promote)` (pinned before strong), queue drain, initial
  dependent fixed-point scanning, `AfterGcScanRoots`, short weak clearing, finalization and
  drain, a dependent fixed-point rescan, long weak/dead-dependent clearing, and the
  single-threaded sync-block weak callback. Partial collections also snapshot region survival,
  mark dirty cards in older SOH, LOH, and POH regions, drain the resulting queue, and retain cards
  that still contain cross-generation references. The WKS overflow-recovery leaves and
  their
  generation-size, total-heap-size, and promoted-byte accounting closure are translated without
  routing. The bounded WKS `CFinalize` storage, registration/dequeue, F-reachable scan, direct
  `ScanForFinalization`, and generation-range relocation closure are translated and
  classification is invoked by this prefix; finalizer scheduling remains deferred;
  `GC_CONFIG_DRIVEN` interesting-data-point updates are omitted because the
  managed NativeAOT build defines neither that symbol nor its diagnostic storage; the
  `_DEBUG && VERIFY_HEAP` `verify_pinned_queue_p` assignment and verification-only state remain
  deferred; `verify_pins_with_post_plug_info` is present under the native guard as a no-op
  because NativeAOT does not build that state)
- `plan_phase.cpp` (the dependency-free `is_induced_blocking`,
  `relative_index_power2_plug`, `relative_index_power2_free_space`, `oddp`, and `logcount`
  prefix helpers; direct WKS brick-tree insertion and brick-table updates; plus the WKS
  `get_gen0_end_space`/`get_gen0_end_plan_space` accounting,
  direct/saved-plug padding bridge, region-survivor snapshot/delta helpers, and the bounded
  `USE_REGIONS && !MULTIPLE_HEAPS` synchronous foreground plan-phase and compaction-policy
  closures are translated. Plan construction preserves pinned-plug
  conversion and saved gap records, remaining-pin/region demotion, SIP decisions/sweeping,
  marked/pinned clearing, allocation calls, relocation flags, brick trees/sentinels, and region
  plan generations with pre-mutation validation. The
  WKS full-GC LOH dependency closure adds the native pin queue state, growth/order/decay,
  allocation-limit clipping, padding-aware fit and condemned allocation, movable/pinned object
  planning, pinned-gap recording, relocation-distance metadata, segment fallback, and read-only
  prefix handling. Partial planning adds older-generation allocator snapshot/commit-or-restore,
  promotion/demotion and condemned allocation. The bounded orchestration continues in native
  order through partial or full SOH compact/sweep completion, full-GC UOH handling, region and
  ephemeral bounds, finalizer fill pointers, handle aging/rejuvenation, pinned gaps, cards, and counters. The
  policy closure preserves region fragmentation and pinned-gap accounting, strict
  region-capacity and hard-limit comparisons, compaction-space/productivity decisions, reason
  precedence, high-memory thresholds, no-GC expansion signaling, and condemned/full-GC
  ephemeral-fit boundaries. The completed bounded plan phase is routed for synchronous Gen0,
  Gen1, and Gen2 collections)
- `relocate_compact.cpp` (the allocation-free `memcopy` relocation primitive, card-copy/clear
  dispatch leaf, pinned-queue `get_next_pinned_entry` and `get_oldest_pinned_entry` handoffs, and
  dependency-closed WKS `USE_REGIONS` `should_check_brick_for_reloc`,
  `check_demotion_helper_sip`, `check_demotion_helper`, `loh_object_p`, brick-tree `tree_search`, and
  `relocate_address` leaves are translated without relocation routing; the next dependency-closed
  non-compacting UOH slice adds `AlignQword`, the NativeAOT-disabled collectible-class demotion
  check, `reloc_survivor_helper`, its allocation-free descriptor callback, and
  `relocate_in_uoh_objects` for writable LOH/POH segments while preserving read-only and
  pointer-free-object skips; the dependency-closed plug-level SOH slice adds normal multi-object
  walking, pre/post shortened-object saved-reference replay, truncated-last-object short-bit
  replay, the pre-plug one-pointer relocation lookup adjustment, and the direct captured-lambda
  context adapter; the next direct traversal slice adds `CObjectHeader::IsFree`,
  `relocate_args`, allocation-free SIP descriptor-walk context/adapters,
  `relocate_advance_to_non_sip`, in-order `relocate_survivors_in_brick`, and
  generation/segment `relocate_survivors`, preserving plug boundaries, pinned-queue handoff,
  cross-brick state, swept-in-plan linear walking and empty-region transitions. The next bounded
  synchronous WKS `USE_REGIONS` foreground slice adds `compact_args`, `get_start_segment`,
  `expand_reused_seg_p`, `gcmemcopy`, `compact_plug`, `compact_in_brick`, and `compact_phase`.
  It preserves header/payload/card copying, shortened pinned pre/post state swaps, in-order
  cross-brick traversal, SIP skipping, generation/segment boundaries, final brick publication,
  saved-pin recovery, and `plan_allocated`-to-`used` transitions. The LOH closure adds marked
  object reference relocation and compaction with native padding, pinned-object order, payload,
  card and write-watch copying, free-gap threading, segment trimming/unlinking, and read-only
  handling. The bounded synchronous WKS `USE_REGIONS` `relocate_phase` slice preserves roots,
  dirty-card relocation for older SOH/LOH objects during partial collections, compacting or
  non-compacting LOH/POH handling during full collections, SOH survivors, finalization data,
  and handle order with one initialized relocation `ScanContext`. It rejects settings mismatch,
  concurrent/background collection, missing heap/finalizer state, and malformed LOH compaction
  state before mutation. Server/card stealing, background roots, and debug-only region-map
  verification remain deferred)
- `sweep.cpp` (the dependency-closed WKS `USE_REGIONS` SOH sweep closure is translated:
  normal-plan promotion and special-sweep retention, swept-in-plan segment skipping during the
  brick walk, positive highest-plug rewrites, negative brick resets, generation free-list
  threading, SIP regional free-list handoff and flag clearing, empty-region return/replacement,
  final generation head/tail rebuilding, allocation-pointer reset, and the post-walk
  `ephemeral_heap_segment`/`alloc_allocated` publication. `thread_gap` now preserves the native
  card clearing and Gen2 reset policy. UOH marked-object sweep, writable-tail trim, non-start
  empty-region unlink, and deferred return through the existing region free-list path remain
  translated too. Planning and collection routing remain deferred)
- `collect.cpp` (the bounded WKS `USE_REGIONS` synchronous foreground `garbage_collect` and `gc1`
  driver is translated and routed, including allocation-context fixing, record/mechanism
  initialization, requested/budget-elevated condemnation, mark/plan completion,
  pinned-allocation adjustment, post-GC accounting, full-GC UOH cleanup,
  range/write-barrier publication, timestamps, and `GcStartWork`/`GcDone`; server, background,
  diagnostics, BGC servo tuning, and dynamic heap count remain deferred)
- `no_gc.cpp`

Beyond its translated dependency-free prefix helpers, `plan_phase.cpp` is the largest and
highest-risk single translation. Keep its function ordering and control flow aligned with C++ so
reviews can compare the two implementations directly.

**Complete when:** foreground collections mark, plan, relocate or sweep, reclaim memory, and
preserve all heap invariants under checked-build verification.

The routed NativeAOT smoke now confirms forced Gen0/Gen1/Gen2 collection, old-to-young card
roots, stack- and handle-only-root survival, strong-handle and GC-static spine relocation, weak
reclamation, pinned-handle stability, dependent-secondary survival, finalization, allocation
after collection, and entrypoint suspension ownership. Full-plan validation derives the WKS
marked-address range from SOH, LOH, and POH objects; omitting UOH marks previously rejected a
valid full collection before relocation. The bounded post-GC accounting now records the
full-blocking finalizable promoted count rather than reporting the concurrently draining live
finalizer queue through `GC.GetGCMemoryInfo()`. The API smoke also validates accurate heap,
fragmentation, promotion, finalization, generation, memory-load, collection-count, and pause
snapshots; latency-mode round trips; runtime hard-limit refresh; and repeated allocation and
collection after those queries.

### 10. Concurrency and tuning

**Status: In progress -- WKS production closure and initialized server multi-heap foundation**

The WKS software-write-watch revisit, native mark-array final closure,
concurrent region sweep, foreground Gen0/Gen1 coordination, allocation and memory-pressure
triggers, reusable native-event worker, dynamic budget retuning, hard-limit refresh, and public
memory/pause/latency reporting are routed.

Translate:

- `background.cpp`
- `finalization.cpp`
- `dynamic_tuning.cpp`
- `dynamic_heap_count.cpp`
- Server GC multi-heap paths

The C++ `MULTIPLE_HEAPS` and `SERVER_GC` conditionals sometimes change fields between static and
instance storage. The C# representation must preserve behavior while remaining source-comparable;
prefer an explicit always-instance representation where required by the language.

The first server slice selects the active x64 Linux
`SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT` feature chain. It adds server-generated
layout constants and native verification, `System.Private.GC.Server`,
`Runtime.ManagedServerGC`, a server-aware selector archive, and the `IlcManagedServerGC` option.
The same server-capable runtime continues to choose managed WKS when `DOTNET_gcServer=0`.

Server initialization now owns `n_heaps`, `n_max_heaps`, and `g_heaps`; creates one five-
generation `gc_heap`, initial region set, SOH/UOH lock set, free-region array, finalization queue,
handle table, and dependent-handle context per heap; and publishes the server and dynamic-heap-
count feature bits through DAC. The ported processor map selects allocation-context home and
allocation heaps, and allocation refills from the selected heap's own Gen0/LOH/POH regions.
Worker creation preserves non-suspendable native thread creation, optional affinity, priority
boost, start/suspend events, join/coordinator state, and shutdown wake/join behavior. The
dynamic heap-count sample/history schemas and native defaults are present; DATAS starts with one
active heap unless `GCHeapCount` fixes the active count. Runtime heap-count changes remain
deferred.

The routed WKS slice now resets software write watch over committed segment extents, revisits
dirty pages and cards during concurrent marking, performs the final root/handle/finalization
closure against the background mark array, and restarts the runtime before sweeping ephemeral,
SOH, LOH, and POH regions. Sweep preserves per-segment background boundaries, allocation locks,
free-list threading, sweep cursors, mark clearing, and background accounting. UOH allocations
made during planning publish their BGC mark bit, and older/UOH budget exhaustion can start a
non-blocking background Gen2. The worker is persistent: a native manual-reset event parks the GC
thread with no managed reverse-P/Invoke frame and re-enters the direct managed callback for each
cycle; shutdown signals and joins that same worker.

Foreground allocation-triggered and explicit blocking Gen0/Gen1 collections now run while BGC
is active. They preserve and restore the BGC mechanisms under the GC lock, copy or clear
background mark bits while relocating, classify swept/current/unswept regions through the native
sweep cursor, and maintain the 64-bit added and doubly-linked Gen2 free lists. The BGC worker
performs its foreground yield entirely in a native transition helper and retains the GC lock
through ephemeral sweep before exposing concurrent Gen2 sweep. Empty regions are unlinked during
concurrent sweep and returned through the deferred region path at the next collection. Background
generation/history data remains separate from intervening foreground records until post-sweep
publication.

The WKS `USE_REGIONS` tuning closure now ports `desired_new_allocation`,
`compute_new_dynamic_data`, incoming-budget adjustment, low-latency young-generation budgets,
memory-load sampling, explicit/percentage/per-object-heap/container hard limits, and refresh
rollback. Collection history records ephemeral, full-blocking, and double-buffered background
snapshots with before/after generation size and fragmentation, promoted/pinned/finalization
counts, memory load, both pause intervals, pause percentage, and cumulative suspension time.
The `IGCHeap` memory-load, generation-budget, latency-mode, reserved-VM, memory-info, pause,
last-GC timing, and current-object-size slots consume that state. This also closes the heap-side
timing dependency used by NativeAOT `GC.AddMemoryPressure`.

The WKS `USE_REGIONS` no-GC-region production APIs are translated from `no_gc.cpp`.
Preparation serializes start/end calls, scales and aligns SOH/LOH budgets, reserves committed
region space, honors `disallowFullBlockingGC`, and either skips the initiating collection with
native counter updates or restores budgets after it. Allocation exhaustion, induced/automatic
collection accounting, exact start/end statuses, callback budget withholding, abandoned
callback cleanup, and lock-free finalizer-work publication preserve native state transitions.

Foundation coverage pins every routed state, the foreground/BGC settings handoff, mark-range and
sweep-cursor state, allocation-triggered scheduling, budget retuning, hard-limit validation,
metric snapshots, latency effects, and reuse of one worker for successive cycles. The dedicated
NativeAOT smoke mutates and allocates during background work, runs blocking
Gen0/Gen1 collections across active BGC cycles, checks a pre-existing-to-new card edge,
strong/pinned handles and finalization, recycles empty UOH regions, runs successive explicit BGC
cycles through the same worker, forces allocation- and memory-pressure-triggered cycles, and
checks memory/pause APIs and active-BGC latency changes.

Foundation coverage verifies the server feature symbols and x64 layouts, dynamic heap-count
defaults, and instance-owned per-heap state. A NativeAOT smoke requests `DOTNET_gcServer=1`,
starts two server heaps/workers, and validates home-heap selection from multiple allocating
threads. No collection entry point is routed by this slice.

The server collection coordination and barrier infrastructure that precedes parallel
mark/plan/relocate is now translated without routing any collection. The full `gcinternal.h`
`t_join` replaces the earlier single-shot join stub: `join_structure`, the `join_type` /
`join_time` / `join_heap_index` enums, `first_thread_arrived`, and the color-based `join`,
reverse `r_join`, `restart`, `r_restart`, and `r_init` methods preserve the spin/hard-wait,
lock-color flip, event-array indexing, and `FATAL_GC_ERROR` shape (`JOIN_STATS` instrumentation
is omitted). The `gc.cpp` `gc_done_event` handshake is present as `set_gc_done`, `reset_gc_done`,
`enter_gc_done_event_lock`, `exit_gc_done_event_lock`, and isolated `wait_for_gc_done`, gated by
`gc_started` and the per-heap `gc_done_event_lock` / `gc_done_event_set`, with
`enable_preemptive` / `disable_preemptive` and the published `g_num_processors`. Focused
Foundation tests pin the join enum values, `join_structure` fields, the `t_join` method surface
and `gc_t_join` field type, and the `gc_done_event` handshake state and coordination methods.

The condemnation coordination -- `generation_to_condemn` and `joined_generation_to_condemn` --
is now translated for the server `SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS`
configuration without routing any collection (`ManagedServerGCCondemn.cs`). The per-heap
`generation_to_condemn` preserves budget/UOH allocation triggers, the low-card-table-efficiency,
low-ephemeral-space, and ephemeral/gen2 fragmentation escalations, the `USE_REGIONS`
`try_get_new_free_region` OOM signal, memory-load and VA-load sampling, provisional/elevation
mode, `HOST_64BIT` almost-max-alloc elevation, the `last_gc_before_oom` blocking path, induced /
induced-noforce reasons, the `BACKGROUND_GC` gen2-too-small blocking heuristic, and per-heap
`gen_to_condemn_reasons` publication. `joined_generation_to_condemn` preserves the cross-heap
`joined_last_gc_before_oom` scan, elevation locking / reduction, provisional-mode gen reduction,
hard-limit LOH fragmentation/reclaim compaction policy, `GCConserveMem` combined-fragmentation
policy, aggressive-induced LOH compaction, background gen2 retraction, and the
`DYNAMIC_HEAP_COUNT` rethreading / initial-gen2 triggers, all writing `gc_data_global` condemn
reasons. The supporting closure -- `dt_low_ephemeral_space_p`, `dt_high_frag_p`,
`dt_estimate_reclaim_space_p`, `dt_estimate_high_frag_p`, `dt_low_card_table_efficiency_p`,
`ephemeral_gen_fit_p`, `estimated_reclaim`, `generation_size`, `generation_unusable_fragmentation`,
`get_new_allocation`, `current_generation_size`, `get_memory_info`, `get_total_gen_*`,
`min_reclaim_fragmentation_threshold`, `min_high_fragmentation_threshold`, and
`try_get_new_free_region` -- is translated with server `n_heaps` scaling. The PER_HEAP fields
`condemned_generation_num`, `blocking_collection`, `elevation_requested`, `generation_skip_ratio`,
`last_gc_before_oom`, and `gen_to_condemn_reasons` are instance-owned in the `MULTIPLE_HEAPS`
build, and `generation_skip_ratio_threshold`, `trigger_initial_gen2_p`, and
`trigger_bgc_for_rethreading_p` are added as isolated state. Focused Foundation tests pin the
condemn-reason enum values, the `gen_to_condemn_tuning` encoding, the decider/tuning method
surface, the per-heap and isolated condemn fields, and the per-heap `generation_skip_ratio`
initialization. The `BGC_SERVO_TUNING`, `STRESS_HEAP`, `STRESS_DYNAMIC_HEAP_COUNT`, and
`HEAP_ANALYZE` branches are excluded exactly as for the active configuration. The
`garbage_collect` `gc_join_generation_determined` join and cross-heap `gen_max` aggregation
that consume these deciders remain deferred with collection routing, as does unification of the
per-heap server free-region list with the shared region free-list path used by the
`try_get_new_free_region` empty-region fallback.

The server mark phase now has its first executable slice: a dependency-closed per-heap mark
engine translated from the SVR compilation of `mark_phase.cpp` and `GCHeap::Promote`
(`ManagedServerGCMarkPhase.cs`) that compiles and unit-tests without routing the overall
collection. It provides per-heap mark storage initialization/cleanup (the `PER_HEAP_ISOLATED`
`g_mark_list` backing via `initialize_shared_mark_list` / `destroy_shared_mark_list`, per-heap
`initialize_mark_stack` / `make_mark_stack`, `initialize_mark_phase_state`,
`setup_mark_state_for_collection`, and `free_server_mark_storage`, wired into server startup and
teardown); the object-walk and marking leaves (`go_through_object` family, `gc_mark` / `gc_mark1`,
the `MULTIPLE_HEAPS` `m_boundary` and empty `m_boundary_fullgc`, `add_to_promoted_bytes`, per-heap
`get_promoted_bytes`, `record_mark_stack_overflow`); the exact/interior/pinned promotion callbacks
(`promote` / `GCHeap::Promote` with `heap_of`, per-heap `find_object` / `clear_gen0_bricks`,
`pin_object`, `mark_object`, `mark_through_object`); the mark queue push/drain/overflow path
(`mark_queue_t` transitions, `mark_object_simple` / `mark_object_simple1` / `drain_mark_queue`,
`process_mark_overflow` and the `n_heaps`-walking `process_mark_overflow_internal`); the per-heap
root/finalizer/strong+pinned handle scan entry point (`mark_phase_scan_roots` calling
`GcScanRoots`, the per-heap `server_finalize_queue->GcScanRoots`, and `GcScanHandles`); and the
server join boundary wiring (`scan_dependent_handles` with the `s_fUnscannedPromotions` /
`s_fUnpromotedHandles` / `s_fScanRequired` latches, the `gc_join_scan_dependent_handles` /
`gc_join_rescan_dependent_handles` joins, and cross-heap overflow reconciliation). `GCScan.cs`,
`HandleTableScan.cs`, and `GCBridge.cs` are shared into the server build behind feature guards by
supplying the server `ManagedGCHeap.IsPromoted` / `GetPromotedBytesForHandleScan` /
`ConcurrentCollectionInProgress` / `IsPromotedForBridge` / `DiagWalkObjectForBridge` members, so
handle-table heap selection follows `sc->thread_number`. The `PER_HEAP_FIELD_SINGLE_GC` /
`MAINTAINED` / `DIAG_ONLY` mark state becomes instance-owned in the `MULTIPLE_HEAPS` build while
`gc_low`/`gc_high` stay `PER_HEAP_ISOLATED`. Focused Foundation tests pin the mark-engine method
surface, the per-heap instance vs. shared-static field ownership, the `promote` and
`scan_dependent_handles` signatures, the `mark_queue_t` method surface, and a behavior test of the
16-slot deferred-marking queue's defer-then-mark-on-eviction transition. The cross-heap
`sort_mark_list` / `merge_mark_lists` / `equalize_mark_lists`, `mark_steal`,
`equalize_promoted_bytes` region rebalancing, and the full `mark_phase` join sequence
(`gc_join_begin_mark_phase` through `gc_join_null_dead_syncblk`) that would route a collection
remain deferred.

The earlier cross-heap post-mark reconciliation that runs in the joined region of `mark_phase`
after every heap finishes promoting its roots and cards (`ManagedServerGCMark.cs`) is unchanged.

Production blockers remain in BGC servo tuning, dynamic heap-count changes after startup,
diagnostic `saved_changed_segs` publication, condemnation-driven collection routing, and server
parallel collection closure.

**Complete when:** background GC, finalization, dynamic tuning, workstation GC, and server GC
match the native collector's synchronization and scheduling behavior.

### 11. Diagnostics and runtime integration

**Status: In progress -- active WKS `USE_REGIONS` diagnostics and event integration complete**

The workstation region path now includes:

- DAC 2.8 publication for generation layouts, heap/generation/segment addresses, handle maps,
  finalization, background mark/sweep state, region free lists, bookkeeping, OOM history, and
  runtime-structure validity.
- Finalizer, handle, dependent-handle, generation, and segment diagnostic walks;
  `DiagGetGCSettings`; and generation-with-range publication.
- Public and private event control plus collection start/range/trigger/end/stats, allocation,
  pinning, segment, committed-usage, foreground/background history, BGC phase, wait, handle, and
  notification events. Event payloads remain allocation-free in collector code.
- Foundation coverage for DAC addresses/layouts, diagnostic handle flags, range/settings/history
  payloads, and event thresholds, plus NativeAOT EventPipe smokes for blocking and background GC.

Still deferred:

- Server `MULTIPLE_HEAPS` and non-region diagnostics.
- cDAC `gc_descriptor`, changed-segment publication, global per-phase timing, and fit-bucket
  diagnostic payloads.
- GCStress, heap verification, survivor analysis, and dump-only verification helpers; the
  collection guards for these modes remain faithful.

Translate and integrate:

- `diagnostics.cpp`
- `gcee.cpp`
- `interface.cpp`
- `gcbridge.cpp`
- DAC/cDAC data descriptors, including `PopulateDacVars`, which fills in the `GcDacVars`
  translated in stage 2
- GC events
- GCStress
- Heap verification

**Complete when:** diagnostics, eventing, stress modes, heap verification, dumps, and debugger
inspection provide equivalent information for the managed and C++ collectors.

### 12. `vxsort`

**Status: In progress -- the active x64 path and scalar fallback are translated; ARM64 NEON
remains**

The managed implementation preserves the native GC-facing pointer-key interface, AVX2 and
AVX-512 crossover thresholds, small-sort boundaries, median-of-three and depth-limit behavior,
and scalar fallback. It uses guarded unaligned x64 vector scans with scalar tails, and the WKS
region planning phase now calls it at the same usable-mark-list point as `plan_phase.cpp`.
Differential Foundation coverage includes randomized, duplicate-heavy, ordered, reversed,
threshold-boundary, guard-word, and adversarial inputs; a planning-phase test covers the
mark-list mechanism bit and the full-GC/overflow exclusions. The ARM64 NEON partition remains
on the scalar fallback.

**Complete when:** differential sorting tests pass on all supported instruction sets and
performance is comparable to the native implementation.

### 13. NativeAOT and ILC integration

**Status: Collector-free bootstrap and staged production initialization complete; collector integration incomplete**

Continue the existing integration work:

- Compile the managed GC into the NativeAOT runtime.
- Keep the managed-only `Runtime.ManagedGC` archive synchronized as additional native GC modules
  are replaced.
- Ensure ILC emits GC code without unsupported runtime dependencies.
- Preserve cooperative-mode, no-transition, and no-suspension regions.
- Keep an opt-in switch until the managed collector passes the full validation matrix.
- Remove the temporary native `gcenv`, configuration, and common support as their managed
  translations become usable.

**Complete when:** supported NativeAOT applications build and run without compiling or linking
any C++ GC implementation or environment support, while the default C++ path remains available
during rollout.

### 14. Validation

**Status: Started; expands with every stage**

Validation is cross-cutting rather than a final phase:

- Differential tests against the C++ GC.
- Existing tests under `src/tests/GC`.
- GCSimulator and GCStress.
- Checked-build heap verification enabled during development.
- Workstation and server GC configurations.
- Concurrent and background collection stress.
- DAC/cDAC and diagnostics tests.
- NativeAOT smoke tests that prove the managed path does not fall back.
- Focused throughput, allocation-rate, pause-time, and memory-usage benchmarks.
- Architecture coverage for x64, arm64, and every subsequently enabled target.

**Complete when:** the managed collector passes the same correctness, stress, diagnostics, and
performance gates as the C++ NativeAOT collector.

## Major risks

- **Bootstrapping:** managed code running during suspension cannot depend on allocation, type
  initialization, GC transitions, or helpers that require a functioning collector.
- **Source drift:** CoreCLR continues to use and modify the C++ GC. Mechanical correspondence and
  automated layout/interface checks are required to keep the port synchronized.
- **DAC/cDAC compatibility:** diagnostics consume GC state by layout and semantic contract.
  Descriptor changes must accompany structure changes.
- **Performance:** bounds checks, aliasing assumptions, inlining, and code-generation differences
  must be measured phase by phase.
- **Platform behavior:** calling conventions, object alignment, write barriers, atomics, and
  virtual memory behavior vary across architectures and operating systems.

## Landing strategy

Each change should port one coherent C++ module or dependency layer, retain source correspondence,
add differential validation, and leave the managed-GC configuration buildable. Avoid combining a
mechanical translation with algorithmic cleanup or redesign. Temporary native shims and bootstrap
implementations should have a clear deletion point in the relevant stage above.
