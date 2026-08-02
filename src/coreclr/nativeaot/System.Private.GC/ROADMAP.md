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
  `nativeaot/Runtime/gcenv.managed.cpp`. Virtual memory is no longer among them.
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

**Status: In progress -- the interface is complete, virtual memory is translated, the rest of
the platform implementations are not**

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
  `nativeaot/Runtime/gcenv.managed.cpp`. One shim remains and belongs to submodule 7 below
  rather than to this one: `ManagedGC_NUMA_BindMemoryPolicy` is the `mbind` half of
  `VirtualCommitInner` verbatim, which needs `g_numaAvailable`, `g_highestNumaNode` and
  `BindMemoryPolicy` of `gc/unix/numasupport.cpp`.
- Focused xUnit coverage of every piece above that is pure computation, in
  `tests/GCEnvironmentTests.cs`, and of the whole virtual memory port -- flag translation,
  alignment over-allocation and trimming, failure paths, and a reserve/commit/write/reset/
  decommit/release exercise on raw pages -- in `tests/GCVirtualMemoryTests.cs`, which runs the
  shipping bodies over recording substitutes for their libc and Win32 declarations.

#### Remaining submodules

Each item below is a native module that `nativeaot/Runtime/gcenv.managed.cpp` currently forwards
to. The managed declaration already exists and does not change when the implementation lands;
only the body and its shim do. They are listed in the order they become blocking.

1. **Write watch** -- `SupportsWriteWatch`, `ResetWriteWatch`, `GetWriteWatch`. Windows only;
   Unix returns false. Blocks the concurrent parts of stage 10.
2. **Events and locks** -- the `GCEvent::Impl` of `gc/unix/events.cpp` and its Win32 counterpart,
   and the `minipal_mutex` behind `CLRCriticalSection`. A managed implementation needs a futex or
   equivalent that is safe to call with the world suspended. Blocks stages 9 and 10.
3. **Sleep and yield** -- `Sleep`, `YieldThread`.
4. **Memory limits** -- `GetPhysicalMemoryLimit`, `GetMemoryStatus`, `GetCacheSizePerLogicalCpu`.
   These read cgroup v1/v2 files (`gc/unix/cgroup.cpp`), `sysconf`, `sysctl` and Windows job
   objects. Blocks the hard-limit and dynamic tuning parts of stage 10.
5. **Timers** -- `QueryPerformanceCounter`, `QueryPerformanceFrequency`,
   `GetLowPrecisionTimeStamp`.
6. **Processor counts and identity** -- `GetTotalProcessorCount`, `GetMaxProcessorCount`,
   `GetCurrentProcessorNumber`, `CanGetCurrentProcessorNumber`, `GetCurrentProcessId`,
   `GetCurrentThreadIdForLogging`.
7. **Affinity, NUMA and CPU groups** -- `SetThreadAffinity`, `BoostThreadPriority`,
   `SetCurrentThreadIdealAffinity`, `GetCurrentThreadIdealProc`, `SetGCThreadsAffinitySet`,
   `CanEnableGCNumaAware`, `GetNumaInfo`, `CanEnableGCCPUGroups`, `GetProcessorForHeap`,
   `GetCPUGroupInfo`, `ParseGCHeapAffinitizeRangesEntry`, plus `gc/unix/numasupport.cpp` and the
   `ManagedGC_NUMA_BindMemoryPolicy` shim that `VirtualCommit` still calls. Blocks server GC in
   stage 10.
8. **Initialization** -- `Initialize` and `Shutdown`. NativeAOT calls the C++ ones from
   `PalInit`, so the managed GC never calls these; they land last, together with moving that call
   out of `PalInit`.
9. **Heap allocation for the environment** -- `ManagedGC_AllocZeroed` and `ManagedGC_Free`, which
   stand in for the `new (nothrow) uintptr_t[]` and `delete[]` of `AffinitySet`. They can only go
   away once the GC has memory of its own to take that bitset from, which is stage 7.

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
