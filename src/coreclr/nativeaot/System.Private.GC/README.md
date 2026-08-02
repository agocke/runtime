# System.Private.GC

This library is the in-progress C# port of the garbage collector that NativeAOT currently
compiles from C++ (`src/coreclr/gc`). The goal is a GC that is compiled by ILC alongside the
rest of the runtime, so that no C++ toolchain is required to build or modify it.

The port proceeds bottom-up: leaf modules with no dependency on the GC/EE interface or on the
`gcpriv.h` data structures are ported first, then the environment layer, then the heap itself.
Each source file here corresponds to one or more files in `src/coreclr/gc`; the header comment
of every file records which ones.

See [ROADMAP.md](ROADMAP.md) for the dependency-ordered port plan, completion criteria, and
validation strategy.

## Rules for code in this library

The GC runs while the world is suspended and while the heap is in an inconsistent state, so
managed code here is severely restricted. Code in this library must:

* Never allocate managed memory, and never hold or produce a GC reference. All heap addresses
  are `byte*`/`nuint`, never `object`. This keeps the ported code compilable without a GC
  underneath it.
* Use `unsafe` pointer code that mirrors the C++ pointer arithmetic one-for-one. Fidelity to
  the original is more important than idiomatic C#: a mechanical translation can be diffed
  against the C++ when the C++ changes.
* Avoid anything that requires runtime services that are unavailable during a collection:
  exceptions, type loading, virtual dispatch through managed interfaces, static constructors
  (use explicitly initialized statics), `string`, LINQ, and generics over reference types.
* Keep the C++ names (including `snake_case` where the C++ uses it) when porting a type whose
  layout or naming is load-bearing, so the correspondence stays reviewable. New helper APIs
  follow normal .NET naming.

## Status

Ported so far:

| C# file | Ported from |
| --- | --- |
| `GCEventEnums.cs` | `gcinterface.h` (event level/keyword/provider enums) |
| `GCEventStatus.cs` | `gceventstatus.h`, `gceventstatus.cpp` |
| `IntroSort.cs` | `introsort.h` |
| `Interface/GCInterfaceEnums.cs` | `gcinterface.h`, `gcinterface.ee.h` (enums) |
| `Interface/GCInterfaceStructs.cs` | `gcinterface.h`, `gcinterface.ee.h` (shared structs) |
| `Interface/GCInterfaceVtables.cs` | `gcinterface.h`, `gcinterface.ee.h`, `gc.h` (abstract classes) |
| `Interface/GCInterfaceDac.cs` | `gcinterface.dac.h` (`GcDacVars` and the DAC analogue types) |
| `Interface/GCInterfaceLayout.cs` | layout check against `GCInterfaceOffsets.h` |
| `Interface/GCToEEInterface.cs` | `gcenv.ee.standalone.inl` |
| `GCConfig.cs` | `gcconfig.h`, `gcconfig.cpp` |
| `ManagedGCEntryPoints.cs` | `gcload.cpp` (`GC_VersionInfo`, `GC_Initialize`) |
| `Environment/GCEnv.Base.cs` | `env/gcenv.base.h`, plus `ParseIndexOrRange` of `gcconfig.cpp` |
| `Environment/GCEnv.Volatile.cs` | `env/volatile.h` (the free functions) |
| `Environment/Interlocked.cs` | `env/gcenv.interlocked.h`, `env/gcenv.interlocked.inl` |
| `Environment/GCEnvStructs.cs` | `env/gcenv.structs.h` |
| `Environment/AffinitySet.cs` | `env/gcenv.os.h` (`AffinitySet`) |
| `Environment/GCEvent.cs` | `env/gcenv.os.h` (`GCEvent`) |
| `Environment/GCEnvSync.cs` | `env/gcenv.os.h` (`CLRCriticalSection`), `env/gcenv.sync.h` |
| `Environment/GCToOSInterface.cs` | `env/gcenv.os.h` (`GCToOSInterface`) |
| `GCHeapMemory.cs` | `gcenv.ee.cpp` write-barrier publication, `card_table.cpp` (tables only) |
| `ManagedGCHeap.cs` | `gcinterface.h` `IGCHeap` (non-collecting subset) |
| `ManagedGCHandleManager.cs` | `objecthandle.cpp`, `gchandletable.cpp` (flat-table subset) |

`gcinterface.dac.h` is translated except for `dac_generation` and `dac_gc_heap`, which are
generated from the `dac_generation_fields.h` / `dac_gcheap_fields.h` field lists and therefore
name `gcpriv.h` types, and `dac_handle_table` and `dac_handle_table_segment`, whose array fields
are sized by the constants of `handletableconstants.h`. Those arrive with the core data
structures and with the handle table respectively. Nothing populates a `GcDacVars` yet:
`PopulateDacVars` publishes the addresses of the collector's data structures, which this heap
does not have.

`gceventstatus.h` is ported except for two pieces that are not leaves. `DebugDumpState` is a
`fprintf` dump behind the commented-out `TRACE_GC_EVENT_STATE`, and there is no string-free way
to write it until the GC's tracing support is ported. `FireDynamicEvent` and the
`KNOWN_EVENT`/`DYNAMIC_EVENT`/`EVENT_ENABLED`/`FIRE_EVENT` macros need `gcevents.h` and
`gcevent_serializers.h`, which belong with the rest of the standalone GC event plumbing.

## The environment layer

`Environment/` is the port of `gcenv`: everything the collector gets from below it rather than
from the EE. It is split in two by what the code actually does.

Pure computation is translated outright and is exercised by
`tests/GCEnvironmentTests.cs`: the alignment helpers, bit scans, HRESULT helpers and constants of
`gcenv.base.h`; the `Interlocked` class of `gcenv.interlocked.h`/`.inl`; the `VolatileLoad` /
`VolatileStore` family of `volatile.h`; the `AffinitySet` bitset of `gcenv.os.h`; and
`ParseIndexOrRange` plus `ParseGCHeapAffinitizeRanges` from `gcconfig.cpp`.

Everything that reaches the operating system is declared with the C++ signature and forwarded,
for now, to a one-line shim in `nativeaot/Runtime/gcenv.managed.cpp` that calls the existing C++
`GCToOSInterface` in `gc/unix/gcenv.unix.cpp` or `gc/windows/gcenv.windows.cpp`. Those shims are
the whole retained-native surface of this layer:

* one per `GCToOSInterface` method (`ManagedGC_OS_*`) -- virtual memory, write watch, sleep and
  yield, processor number and affinity, thread priority and ids, cache and memory limits, the
  performance counter, processor counts, NUMA and CPU groups, and the platform-specific affinity
  range entry parser;
* one per `GCEvent` method (`ManagedGC_GCEvent_*`);
* four for `CLRCriticalSection` (`ManagedGC_CriticalSection_*`);
* `ManagedGC_AllocZeroed` / `ManagedGC_Free`, which stand in for the `new (nothrow) uintptr_t[]`
  and `delete[]` that `AffinitySet::Initialize` and `~AffinitySet` use, and which are the only
  heap allocation the managed GC performs.

Each of them is deleted when the platform code behind it is ported; that is the remainder of
plan step 3 in [ROADMAP.md](ROADMAP.md), which lists the modules by name. The calls are
`[RuntimeImport]`, so they are direct calls to linked symbols with no marshalling and no GC mode
transition -- what the C++ GC gets for free by being native code, and what a `[DllImport]` would
not give.

Three shapes differ from the C++ on purpose, each for a reason C# forces:

* `AffinitySet` is a struct, so it has no destructor; `~AffinitySet` becomes an explicit
  `Destroy()`. Its two fields are laid out exactly like the C++ ones, because
  `SetGCThreadsAffinitySet` passes one across to the platform layer and hands back the platform
  layer's own.
* `CLRCriticalSection` holds a pointer to a natively allocated critical section instead of
  embedding a `minipal_mutex` by value. The embedded object is a `pthread_mutex_t` or a
  `CRITICAL_SECTION`, whose size differs per operating system, and `GCInterfaceOffsets.h` carries
  one value per pointer size rather than one per platform. Nothing passes a `CLRCriticalSection`
  across a boundary, so no layout depends on the difference.
* `EEThreadId` stores an OS thread id on both platforms rather than a `pthread_t` on Unix and a
  Windows thread id on Windows. It is only read by the debug-only lock-ownership assertions.

`gcenv.object.h` is deliberately **not** ported. NativeAOT does not use it: its own `gcenv.h`
supplies `MethodTable`, `Object`, `ObjHeader` and `ArrayBase` from `Runtime/inc/MethodTable.h` and
`Runtime/ObjectLayout.h` instead, and those are the definitions the collector and the EE actually
agree on. Translating `gcenv.object.h` would produce an object model the runtime does not have;
the NativeAOT one is ported with the core data structures.

`CLREventStatic` of `gcenv.sync.h` is not ported either -- the GC does not use it. Only the
NativeAOT runtime does, from its own C++.

`GCToOSInterface::Initialize` and `Shutdown` are declared but never called by the managed GC:
NativeAOT initializes the C++ `GCToOSInterface` from `PalInit`, before any managed code runs.

## Testing the ported leaves

`tests/ManagedGC.Foundation.Tests.csproj` is a regular xUnit project that compiles the
dependency-free leaf sources, the GC/EE interface types and the environment layer directly. Their behavior and layout is
tested independently of the NativeAOT runtime integration smoke test and independently of which
paths the bootstrap heap happens to exercise. `GCInterfaceLayoutTests` covers the layout table as
described under [layout verification](#layout-verification); the rest is behavior.

`IntroSort` always finishes with `insertionsort` over the whole range, so its output is in order
whatever `introsort_loop` did. The test therefore asserts on the properties that are not free:
that the multiset is preserved (a partition or sift that duplicates or drops an entry would be a
mark list entry silently lost), that the ordering is the unsigned one that pointer comparison
gives in both languages, that nothing outside the sorted range is written, and that the
depth-limited recursion terminates. Reaching `heapsort` at all needs input that exhausts
`max_depth`, which no natural input does, so the test carries a vector produced by McIlroy's
quicksort adversary.

### The heap is a bump allocator that never collects

`ManagedGCHeap` implements enough of `IGCHeap` to boot and run an application, and no more. It
reserves one 256 MB region up front and hands it out with an interlocked bump pointer. It never
frees anything, so:

* An application that allocates more than 256 MB gets `OutOfMemoryException`, permanently.
* Finalizers never run. `GC.Collect` performs a real suspend/restart cycle and increments a
  counter, but does not scan roots or reclaim memory.
* `GC.GetTotalMemory` only ever grows -- which is what the smoke test uses to prove that the
  process is running on the current non-collecting managed heap.

`IlcManagedGC` is fail-closed: if `ManagedGC_Initialize` or the later `IGCHeap::Initialize`
fails, runtime startup fails. The selector never falls back to the C++ GC. The native collector
is not included in a managed-GC application link. Native runtime and `gcenv` support remains
temporarily while the corresponding environment modules are ported.

`IGCHeap` slots that a non-collecting heap cannot answer honestly are filled with a fail-fast
stub rather than a plausible-looking wrong answer, so the first caller that needs a real
collector is a crash with a stack trace rather than silent corruption.

The object handed to the EE is an `IGCHeapInternal`, as `GCHeap` is in the C++ GC: its vtable is
the `IGCHeap` slots followed by the four slots `gc.h` adds. The EE only reads the `IGCHeap`
prefix; the extra slots exist so that the layout the GC publishes is the one the collector
modules will expect when they start calling them.

Two pieces are real rather than stubbed, because startup does not work without them:

* **Write-barrier globals.** The heap publishes `g_card_table`, the card bundle table and the
  heap bounds through `GCToEEInterface.StompWriteBarrier`. Both tables are *biased* -- the
  assembly barrier indexes them by absolute address (`dst >> 11` and `dst >> 21`), so the
  published pointer is `table_base - (lowest_address >> shift)`. The card bundle table is
  dereferenced with no null check on every architecture that sets
  `FEATURE_MANUALLY_MANAGED_CARD_BUNDLES`, so it has to exist even though nothing reads the
  cards back.
* **Frozen segments.** `StartupCodeHelpers` fail-fasts if `RegisterFrozenSegment` returns null.
  Frozen segments are kept outside `[lowest_address, highest_address)`, matching the assert in
  `gc_heap::insert_ro_segment`.

### Suspension safety

The managed vtable methods are visible to NativeAOT's code manager as managed code. Without an
additional guard, another thread initiating a GC could suspend one at a managed safe point in the
middle of updating an allocation context, handle free list, frozen segment, or other GC-owned
state. The C++ GC does not have that exposure: an `IGCHeap` call remains cooperative native code
until it returns.

`GCHeapCriticalRegion` preserves the native contract around multi-step mutations. Its runtime
shims set `TSF_DoNotTriggerGc`, which makes both explicit GC polls (`RhpGcPoll2`) and asynchronous
hijacking (`HijackCallback`) leave the thread running until the region exits. The shim preserves a
flag that was already set by its caller, so nested runtime callouts remain valid. Read-only vtable
methods do not need a region because their state is consistent at every instruction; newly ported
methods must enter one before making GC-owned state temporarily inconsistent.

Calls from the managed GC back into `IGCToCLR` and `IGCToCLREventSink` use
`delegate* unmanaged[SuppressGCTransition]`. The C++ GC makes the same calls without changing GC
mode, and preserving cooperative mode is also required while a critical region has
`TSF_DoNotTriggerGc` set.

The thread that actually suspends the EE is excluded from `ThreadStore::SuspendAllThreads`, and GC
worker threads are marked GC-special and are never hijacked. `GarbageCollect` still uses a critical
region: after `SuspendEE` sets the global trap, an explicit poll in the remaining managed method
must not make the suspending thread wait for the collection that only it can finish. Other critical
regions protect mutators that can run on ordinary application threads. The managed-GC smoke test
repeatedly performs the null collector's real suspend/restart cycle while other threads allocate,
covering the integration path and detecting deadlocks or post-suspension allocation corruption. A
future collector that scans allocation contexts while stopped will directly validate their
invariants.

## Building an application against the managed GC

Publish a NativeAOT application with `IlcManagedGC=true`:

```
dotnet publish -r linux-x64 -p:PublishAot=true -p:IlcManagedGC=true
```

or, for an in-tree smoke test, see `src/tests/nativeaot/SmokeTests/ManagedGC`.

The application then runs entirely on the C# heap: startup, module frozen object segments,
statics, threads and every allocation. `IlcManagedGC` selects `Runtime.ManagedGC`, which excludes
the C++ collector, native handle table, GC loader, bridge, scanner, event status, and software
write watch. Applications that do not opt in continue to link `Runtime.WorkstationGC` or
`Runtime.ServerGC`.

### How the linkage works

`GC_Initialize` and `GC_VersionInfo` are plain `extern "C"` symbols, so the managed GC only has
to define equivalents that the linker can resolve:

* `ManagedGCEntryPoints` declares them with `[RuntimeExport]`. That attribute, rather than
  `[UnmanagedCallersOnly]`, because a runtime export is a direct native-to-managed call with no
  reverse-P/Invoke thread attach and no cooperative/preemptive transition -- neither of which is
  available during startup or with the world suspended.

  The same reasoning applies to the vtable slots, which is why they are typed as *managed*
  function pointers (`delegate*<...>`) rather than `delegate* unmanaged<...>`. ILC compiles a
  static method with a blittable signature to the platform C ABI, so native can call it
  directly -- that property is exactly what makes `[RuntimeExport]` work, and taking the
  method's address gives the same entry point the export alias would name. Marking these
  methods `[UnmanagedCallersOnly]` instead is not merely redundant, it is wrong: ILC sets
  `CORJIT_FLAG_REVERSE_PINVOKE` unconditionally for such methods
  (`CorInfoImpl.cs`), so the EE calling `IGCHeap::Alloc` from cooperative mode fail-fasts in
  `Thread::ReversePInvokeAttachOrTrapThread`.
* ILC only emits the symbols when the assembly is passed to `--generateunmanagedentrypoints`,
  which `Microsoft.NETCore.Native.targets` does only under `IlcManagedGC`. The assembly is
  always referenced (it lives in `aotsdk`, which ILC picks up wholesale), but nothing in it is
  reachable otherwise, so default builds are unaffected.
* `nativeaot/Runtime/clrgc.managed.cpp` is the native side. It is the static-linking
  counterpart of `clrgc.enabled.cpp`: the same loader protocol, except the entry points are
  resolved by the linker instead of `PalGetProcAddress`. It is archived as `managedgc-enabled`
  and is mutually exclusive with `standalonegc-enabled`/`standalonegc-disabled`, since all
  three define `InitializeGCSelector`.
* `Runtime.ManagedGC` contains the NativeAOT runtime support still needed by managed-GC
  applications, but omits the C++ collector object files and native modules already replaced by
  managed code. `FEATURE_MANAGED_GC` also removes the default `GC_Initialize` dependency from
  `gcheaputilities.cpp`, preventing static archive extraction from pulling the collector back in.

`IlcManagedGC` is rejected on x86: `WindowsNodeMangler.ExternMethod` leaves runtime export names
undecorated, while a C declaration of the same function references the cdecl-decorated
`_ManagedGC_Initialize`, so the two would not link. Supporting x86 needs an explicit ABI shim.

It is also rejected on ARM32 and WASM, which build the runtime with `FEATURE_64BIT_ALIGNMENT`.
Those targets pass `GC_ALLOC_ALIGN8`/`GC_ALLOC_ALIGN8_BIAS` down to `IGCHeap::Alloc` and expect
the heap to honor them; the bump allocator only aligns to pointer size.

## Layout verification

Types that cross the GC/EE boundary must be laid out exactly like their C++ counterparts.
`GCInterfaceOffsets.h` is the single source of truth for those layouts, and it is consumed three
times:

* `nativeaot/Runtime/GCInterfaceOffsetsVerify.cpp` expands it into `static_assert`s against
  `gcinterface.h`/`gcinterface.ee.h`, so the native build breaks if the C++ layout drifts.
* `src/GCInterfaceOffsets.cspp` is preprocessed by the native build into `GCInterfaceOffsets.cs`,
  a set of C# constants that `GCInterfaceLayout.Verify()` checks the managed structs against
  during GC startup.
* `tests/GCInterfaceLayoutTests.cs` embeds the table and checks the same entries against the
  translated types with plain reflection, so a mistake is reported per entry by `dotnet test`
  without building or booting a runtime.

This mirrors the existing `AsmOffsets.h`/`AsmOffsets.cspp` mechanism used by
`System.Private.CoreLib`, with three additions:

* `GC_ALIGNOF` pins a type's alignment. Offsets pin the internal padding of a type; the size and
  the alignment together pin its trailing padding and how an array or an embedded instance of it
  is placed.
* `GC_SIZEOF` is also applied to every enum that crosses the boundary, since an enum whose
  underlying type changed would silently change every signature and structure it appears in.
* `GC_CONST` pins enumerator values and macros whose name is a valid identifier in both
  languages, and `GC_VALUE` does the same for a C++ expression that is not -- the enumerators of
  a scoped enum, for instance. Enumerators are values rather than offsets, but they are equally
  part of the ABI: they cross the boundary as arguments and return values, several are duplicated
  in `System.GC`, the event keyword bits come from the ETW manifest, and the handle types are
  depended upon by the cDAC contracts.

The layout tests additionally check that the table is *complete*: an unlisted field or enumerator
is not a build break anywhere else, it is simply unverified. The handful of deliberate omissions
are listed in the test with the reason for each.

Vtable order and signatures are verified separately because C++ does not provide a portable
`offsetof` equivalent for virtual slots. `tools/verify-gc-interface-vtables.py` parses the virtual
methods from `gcinterface.h`, `gcinterface.ee.h` and `gc.h`, parses the function-pointer fields
and `SlotCount` values from `GCInterfaceVtables.cs`, and compares all six interfaces on:

* slot count, and for `IGCHeapInternal` that the base `IGCHeap` slots come first;
* slot name and declaration order;
* the full signature, after mapping each C++ type to the C# type the port uses for it, including
  the callback typedefs the script reads out of the same headers;
* the calling convention, which differs by call direction -- see the comment at the top of
  `GCInterfaceVtables.cs`.

The NativeAOT runtime build runs this check before producing `Runtime.WorkstationGC`,
`Runtime.ServerGC` or `Runtime.ManagedGC`, so adding, removing, reordering or re-typing a native
slot without the matching managed change fails the build.
