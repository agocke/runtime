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
| `Interface/GCInterfaceVtables.cs` | `gcinterface.h`, `gcinterface.ee.h` (abstract classes) |
| `Interface/GCInterfaceLayout.cs` | layout check against `GCInterfaceOffsets.h` |
| `Interface/GCToEEInterface.cs` | `gcenv.ee.standalone.inl` |
| `GCConfig.cs` | `gcconfig.h`, `gcconfig.cpp` |
| `ManagedGCEntryPoints.cs` | `gcload.cpp` (`GC_VersionInfo`, `GC_Initialize`) |
| `GCToOSInterface.cs` | `gcenv.os.h` (virtual memory only) |
| `GCHeapMemory.cs` | `gcenv.ee.cpp` write-barrier publication, `card_table.cpp` (tables only) |
| `ManagedGCHeap.cs` | `gcinterface.h` `IGCHeap` (non-collecting subset) |
| `ManagedGCHandleManager.cs` | `objecthandle.cpp`, `gchandletable.cpp` (flat-table subset) |

`ParseGCHeapAffinitizeRanges` is not ported yet: it needs the affinity half of
`GCToOSInterface`, which only the real collector will use.

### The heap is a bump allocator that never collects

`ManagedGCHeap` implements enough of `IGCHeap` to boot and run an application, and no more. It
reserves one 256 MB region up front and hands it out with an interlocked bump pointer. It never
frees anything, so:

* An application that allocates more than 256 MB gets `OutOfMemoryException`, permanently.
* Finalizers never run. `GC.Collect` performs a real suspend/restart cycle and increments a
  counter, but does not scan roots or reclaim memory.
* `GC.GetTotalMemory` only ever grows -- which is what the smoke test uses to tell the managed
  heap apart from the C++ GC it would otherwise fall back to.

Note that the fallback to the C++ GC only covers `ManagedGC_Initialize` declining to provide a
heap. Once it has returned `S_OK`, a later failure in `IGCHeap::Initialize` -- reserving or
committing the 256 MB -- fails runtime startup outright rather than falling back.

`IGCHeap` slots that a non-collecting heap cannot answer honestly are filled with a fail-fast
stub rather than a plausible-looking wrong answer, so the first caller that needs a real
collector is a crash with a stack trace rather than silent corruption.

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
statics, threads and every allocation. The C++ GC is still linked in and is still the default;
`IlcManagedGC` only changes which one `InitializeGCSelector` hands back.

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

`IlcManagedGC` is rejected on x86: `WindowsNodeMangler.ExternMethod` leaves runtime export names
undecorated, while a C declaration of the same function references the cdecl-decorated
`_ManagedGC_Initialize`, so the two would not link. Supporting x86 needs an explicit ABI shim.

It is also rejected on ARM32 and WASM, which build the runtime with `FEATURE_64BIT_ALIGNMENT`.
Those targets pass `GC_ALLOC_ALIGN8`/`GC_ALLOC_ALIGN8_BIAS` down to `IGCHeap::Alloc` and expect
the heap to honor them; the bump allocator only aligns to pointer size.

## Layout verification

Types that cross the GC/EE boundary must be laid out exactly like their C++ counterparts.
`GCInterfaceOffsets.h` is the single source of truth for those layouts, and it is consumed twice:

* `nativeaot/Runtime/GCInterfaceOffsetsVerify.cpp` expands it into `static_assert`s against
  `gcinterface.h`/`gcinterface.ee.h`, so the native build breaks if the C++ layout drifts.
* `src/GCInterfaceOffsets.cspp` is preprocessed by the native build into `GCInterfaceOffsets.cs`,
  a set of C# constants that `GCInterfaceLayout.Verify()` checks the managed structs against.

This mirrors the existing `AsmOffsets.h`/`AsmOffsets.cspp` mechanism used by
`System.Private.CoreLib`.

Vtable order is verified separately because C++ does not provide a portable `offsetof` equivalent
for virtual slots. `tools/verify-gc-interface-vtables.py` parses the virtual methods from
`gcinterface.h` and `gcinterface.ee.h`, parses the function-pointer fields and `SlotCount` values
from `GCInterfaceVtables.cs`, and compares all five interfaces by name and declaration order. The
NativeAOT runtime build runs this check before producing `Runtime.WorkstationGC` or
`Runtime.ServerGC`, so adding, removing, or reordering a native slot without the matching managed
change fails the build.
