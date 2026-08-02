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
- The current heap is a fixed-size, non-collecting bump allocator with a flat handle table.
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

Two groups of `gcinterface.dac.h` types are deferred rather than missing. `dac_generation` and
`dac_gc_heap` are generated from the `dac_generation_fields.h` / `dac_gcheap_fields.h` field
lists, which name `gcpriv.h` types and therefore belong with stage 6. `dac_handle_table` and
`dac_handle_table_segment` are sized by `handletableconstants.h` and belong with stage 5.
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
  `nativeaot/Runtime/gcenv.managed.cpp`. One shim remains and belongs to submodule 5 below
  rather than to this one: `ManagedGC_NUMA_BindMemoryPolicy` is the `mbind` half of
  `VirtualCommitInner` verbatim, which needs `g_numaAvailable`, `g_highestNumaNode` and
  `BindMemoryPolicy` of `gc/unix/numasupport.cpp`.
- Write watch: `SupportsWriteWatch`, `ResetWriteWatch` and `GetWriteWatch`, from
  `gc/unix/gcenv.unix.cpp` and `gc/windows/gcenv.windows.cpp`. Windows calls
  `GetSystemInfo`/`GetWriteWatch`/`ResetWriteWatch` directly, as `[RuntimeImport]`s, and keeps
  the C++ shape: feature detection is the same `MEM_WRITE_WATCH` probe reservation of one
  allocation granularity, released again; `GetWriteWatch` passes the same
  `WRITE_WATCH_FLAG_RESET`, treats a zero return as success -- it is an error code, not a
  `BOOL` -- and asserts the reported granularity against `OS_PAGE_SIZE`; the in/out count is
  `ULONG_PTR`, which is `nuint`, so it needs no conversion. `g_SystemInfo.dwAllocationGranularity`
  is read from `GetSystemInfo` at the point of use rather than from a cached global, because the
  managed `Initialize` that fills the C++ one is submodule 7 below; it is the same machine
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
  submodule 5 below, because the managed GC has no allocator of its own yet. The pthread and
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
  `tests/GCTimerTests.cs`. All of
  them run the shipping bodies over recording substitutes for their libc and Win32 declarations.

#### Remaining submodules

Each item below is a native module that `nativeaot/Runtime/gcenv.managed.cpp` currently forwards
to. The managed declaration already exists and does not change when the implementation lands;
only the body and its shim do. They are listed in the order they become blocking.

1. **cgroup and `/proc` file parsing** -- the six `ManagedGC_CGroup_*` / `ManagedGC_Unix_*`
   leaves the memory limit port left behind: the `CGroup` class of `gc/unix/cgroup.cpp` and the
   `static` `/sys` and `/proc` readers of `gc/unix/gcenv.unix.cpp`. They need a
   `read`/`open`-based parser that allocates nothing, so they wait for the GC to have memory of
   its own (submodule 4 below and stage 7).
2. **Processor counts and identity** -- `GetTotalProcessorCount`, `GetMaxProcessorCount`,
   `GetCurrentProcessorNumber`, `CanGetCurrentProcessorNumber`, `GetCurrentProcessId`,
   `GetCurrentThreadIdForLogging`. The last of these is what the debug-only lock-ownership
   bookkeeping of `CrstStatic` records, which is why those wrapper tests only run in a build
   with asserts disabled.
3. **Affinity, NUMA and CPU groups** -- `SetThreadAffinity`, `BoostThreadPriority`,
   `SetCurrentThreadIdealAffinity`, `GetCurrentThreadIdealProc`, `SetGCThreadsAffinitySet`,
   `CanEnableGCNumaAware`, `GetNumaInfo`, `CanEnableGCCPUGroups`, `GetProcessorForHeap`,
   `GetCPUGroupInfo`, `ParseGCHeapAffinitizeRangesEntry`, plus `gc/unix/numasupport.cpp`, the
   `ManagedGC_NUMA_BindMemoryPolicy` shim that `VirtualCommit` still calls and the
   `ManagedGC_Unix_GetProcessAffinitySet` shim that the cache sizing heuristic still calls.
   Blocks server GC in stage 10.
4. **Heap allocation for the environment** -- `ManagedGC_AllocZeroed` and `ManagedGC_Free`, which
   stand in for the `new (nothrow)` allocations of the environment layer: the `uintptr_t[]` of
   `AffinitySet`, the `GCEvent::Impl` of the event ports, the `minipal_mutex` that the C++
   `CLRCriticalSection` embeds by value, and the `SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]` of the
   Windows `GetLPI`. They can only go away once the GC has memory of its own to take those from,
   which is stage 7.
5. **Initialization** -- `Initialize` and `Shutdown`. NativeAOT calls the C++ ones from
   `PalInit`, so the managed GC never calls these; they land last, together with moving that call
   out of `PalInit`.

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

### 5. Handle table

**Status: Prototype only**

Mechanically translate:

- `handletable.cpp`
- `handletablecore.cpp`
- `handletablecache.cpp`
- `handletablescan.cpp`
- `objecthandle.cpp`
- `gchandletable.cpp`

The current flat table only supports bootstrap scenarios and must be replaced rather than
extended into an independent design. `dac_handle_table` and `dac_handle_table_segment` of
`gcinterface.dac.h` belong here too: their array fields are sized by `handletableconstants.h`.

**Complete when:** handle allocation, caching, scanning, weak/dependent semantics, ref-counted
handles, and per-type behavior match the C++ handle table under differential tests.

### 6. Core GC data structures

**Status: Not started**

Translate the schema from `gcpriv.h` and related headers:

- `heap_segment`
- `generation`
- `alloc_list`
- `dynamic_data`
- Mark structures
- Region tables
- Card and brick tables
- `gcrecord.h`
- `gcdesc.h`

This stage also completes the `gcinterface.dac.h` translation started in stage 2: `dac_generation`
and `dac_gc_heap` are generated from the field lists of `dac_generation_fields.h` and
`dac_gcheap_fields.h`, so they can only be translated once the types those lists name exist.

**Complete when:** the managed types preserve the required native layouts and remain compatible
with DAC/cDAC descriptors such as `dac_gcheap_fields.h`, `dac_generation_fields.h`, and
`datadescriptor`.

### 7. Memory and region management

**Status: Not started**

Translate:

- `memory.cpp`
- `region_allocator.cpp`
- `region_free_list.cpp`
- `regions_segments.cpp`

**Complete when:** reservation, commitment, release, region allocation, free lists, and segment
lifecycle match the C++ collector.

### 8. Allocator and write-barrier interaction

**Status: Bootstrap allocator only**

Translate `allocation.cpp`, including:

- Allocation contexts
- Free lists
- Allocation budgets
- `allocate_more_space`
- Large, pinned, and small object paths
- `card_table.cpp` interaction

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
until the managed version matches its correctness and throughput.

**Complete when:** differential sorting tests pass on all supported instruction sets and
performance is comparable to the native implementation.

### 13. NativeAOT and ILC integration

**Status: Collector-free bootstrap complete; production integration incomplete**

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
