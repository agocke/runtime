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
  Native runtime and `gcenv` support remains temporarily.
- The current heap is a fixed-size, non-collecting bump allocator with a flat handle table.
- Write-barrier globals and frozen segments are initialized sufficiently for application
  startup.
- `GC.Collect` exercises a real `SuspendEE` / `RestartEE` cycle but does not scan or reclaim.
- Managed GC mutations use suspension-safe critical regions.
- GC/EE structure layouts and all five vtable slot lists are verified against the native
  headers.

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

**Status: In progress**

Translate:

- `gcinterface.h`
- `gcinterface.ee.h`
- `gcinterface.dac.h`
- `IGCHeap`, `IGCHandleStore`, `IGCHandleManager`, `IGCToCLR`,
  `IGCHeapInternal`, and `IGCToCLREventSink`
- Shared structures such as `gc_alloc_context`, `segment_info`,
  `WriteBarrierParameters`, and `GCEventFireInfo`

The managed representation uses function-pointer vtables. Native EE callbacks preserve the
cooperative-mode behavior of direct C++ calls.

**Complete when:** all interface methods, signatures, slot order, structure sizes, and field
offsets are automatically checked against the native headers on every supported architecture.

### 3. `gcenv` and platform abstraction layer

**Status: In progress**

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

### 4. Standalone GC infrastructure

**Status: In progress**

Translate:

- `gcconfig.h` and `gcconfig.cpp`
- `gcload.cpp`
- `gccommon.cpp`
- `gcscan.cpp`
- `softwarewritewatch.cpp`
- `gcevent_serializers.h` and `gcevents.h`

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
extended into an independent design.

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
- DAC/cDAC data descriptors
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
