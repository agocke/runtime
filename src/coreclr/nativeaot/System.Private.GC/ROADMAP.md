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
- The current heap is a fixed-size, non-collecting bump allocator with one translated handle
  table.
- The managed GC reads its own configuration: `GCConfig` is translated in full, initialized from
  `ManagedGC_Initialize`, and reported to `GC.GetConfigurationVariables()` through the heap's
  `EnumerateConfigurationValues` slot.
- Write-barrier globals and frozen segments are initialized sufficiently for application
  startup.
- `GC.Collect` exercises a real `SuspendEE` / `RestartEE` cycle but does not scan or reclaim.
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
  `E_OUTOFMEMORY` on a null creation result, and leave the zero-versioned `GcDacVars` untouched
  because the translated heap still has no DAC-published internal structures. The zero DAC
  interface version makes a DAC reject the collector as unsupported rather than interpreting the
  GC/EE interface version as a newer DAC format. Focused tests in
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
  `GetTableStartByteOffset` is declared by the header but has no definition or caller anywhere
  in `src/coreclr`, so it has no C# counterpart; a comment at its would-be call site in
  `GetTableByteSize`'s neighborhood records why. `memcpy` and `memset` become
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
helpers are translated. `softwarewritewatch.h`/`.cpp` are translated in full except for the
declared-but-undefined `GetTableStartByteOffset`; nothing in `Runtime.ManagedGC` calls
`SoftwareWriteWatch` yet, since its only caller in the C++ is `card_table.cpp`, which arrives with
the core heap and region modules of stage 7 -- the port is ready for those call sites when they
land. The remaining `gccommon.cpp` state is
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

#### Remaining

Handle scanning, weak/dependent processing during collection, ref-counted tracing, debug scan
statistics, and multi-heap table selection remain blocked on the core heap and collection state
of stages 6-10.

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
- The dependency-free `gcdesc.h` records and descriptor arithmetic: `val_serie_item`,
  `CGCDescSeries`, and `CGCDesc` size, initialization, and backward series lookup. The
  MethodTable-dependent pointer-counting helper remains tied to object scanning.
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
  `recover_plug_info` remains deferred until `gc_heap::settings.compaction` and the
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
  `thread_free_obj` remains deferred with the free-list object representation. The schema forks on
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
  short-circuiting, gen0 ephemeral publications, and LOH/POH flags. The deferred full heap layout
  is represented by unmanaged bootstrap-owned generation, ephemeral-segment and allocation
  state. `ManagedGCRegionBootstrap` now calls this sequence from production
  `IGCHeap::Initialize`, reserves the configured/default range, creates and extends
  bookkeeping coverage, and releases the range, maps, table and generation storage on every
  failure or shutdown. The bump allocator remains the allocation owner until region allocation
  dependencies are ported.
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

**Status: Bootstrap allocator plus allocation-context leaf**

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
the region-only null-context and contiguous-gen0 branches, limit/accounting update, and
ephemeral used-boundary publication. `grow_heap_segment` and the non-background,
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
`try_allocate_more_space` now has an explicit unmanaged state-machine core over the translated
fit paths. It preserves the SOH/UOH `allocation_state` transitions, generation selection,
allocation flags, commit-failure/short-end/OOM propagation, retry exits, UOH acquisition states,
and the existing budget mutations made by fitting. Its context makes the still-untranslated
heap fields explicit. GC/BGC waits and triggers, full compact collections, dynamic-budget
decisions, locks, UOH acquisition, retry policy, and OOM handling cross an unmanaged function
pointer boundary; without that callback it returns the exact pending native state and deferred
operation. It is not routed into production allocation. `allocate_more_space` remains deferred.
`set_allocation_heap_segment`/`reset_allocation_pointers` cover the region generation schema.
Production allocation still uses `GCHeapMemory`'s bootstrap bump allocator.

The fixed 256 MB bump allocator must be deleted as the translated allocator becomes usable.

**Complete when:** allocation behavior, alignment, accounting, failure paths, and write-barrier
state match the C++ implementation across supported architectures.

### 9. Collection phases

**Status: Not started**

Translate in dependency order:

- `mark_phase.cpp`
- `plan_phase.cpp`
- `relocate_compact.cpp`
- `sweep.cpp`
- `collect.cpp`
- `no_gc.cpp`

`plan_phase.cpp` is the largest and highest-risk single translation. Keep its function ordering
and control flow aligned with C++ so reviews can compare the two implementations directly.

**Complete when:** foreground collections mark, plan, relocate or sweep, reclaim memory, and
preserve all heap invariants under checked-build verification.

### 10. Concurrency and tuning

**Status: Not started**

Translate:

- `background.cpp`
- `finalization.cpp`
- `dynamic_tuning.cpp`
- `dynamic_heap_count.cpp`
- Server GC multi-heap paths

The C++ `MULTIPLE_HEAPS` and `SERVER_GC` conditionals sometimes change fields between static and
instance storage. The C# representation must preserve behavior while remaining source-comparable;
prefer an explicit always-instance representation where required by the language.

**Complete when:** background GC, finalization, dynamic tuning, workstation GC, and server GC
match the native collector's synchronization and scheduling behavior.

### 11. Diagnostics and runtime integration

**Status: Not started**

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

**Status: Not started**

Reimplement `vxsort` with `System.Runtime.Intrinsics` for AVX2, AVX-512, and NEON, preserving the
scalar and platform fallback behavior. The native implementation may remain temporarily linked
until the managed version matches its correctness and throughput. The initial implementation may
be use a simpler sorting algorithm, to verify correctness at the cost of throughput.

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
