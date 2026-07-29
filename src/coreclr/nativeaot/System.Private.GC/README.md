# System.Private.GC

This library is the in-progress C# port of the garbage collector that NativeAOT currently
compiles from C++ (`src/coreclr/gc`). The goal is a GC that is compiled by ILC alongside the
rest of the runtime, so that no C++ toolchain is required to build or modify it.

The port proceeds bottom-up: leaf modules with no dependency on the GC/EE interface or on the
`gcpriv.h` data structures are ported first, then the environment layer, then the heap itself.
Each source file here corresponds to one or more files in `src/coreclr/gc`; the header comment
of every file records which ones.

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

`ParseGCHeapAffinitizeRanges` is not ported yet: it needs `GCToOSInterface`, which is the next
step.

## Building an application against the managed GC

Publish a NativeAOT application with `IlcManagedGC=true`:

```
dotnet publish -r linux-x64 -p:PublishAot=true -p:IlcManagedGC=true
```

or, for an in-tree smoke test, see `src/tests/nativeaot/SmokeTests/ManagedGC`.

The heap itself is not ported yet, so `ManagedGC_Initialize` currently reports that it has no
heap to offer (`S_FALSE`) and the runtime falls back to the C++ GC, which keeps the application
working. The managed path is still exercised: it verifies the interface layout and reads the
whole configuration table through the real `IGCToCLR` vtable during startup.

### How the linkage works

`GC_Initialize` and `GC_VersionInfo` are plain `extern "C"` symbols, so the managed GC only has
to define equivalents that the linker can resolve:

* `ManagedGCEntryPoints` declares them with `[RuntimeExport]`. That attribute, rather than
  `[UnmanagedCallersOnly]`, because a runtime export is a direct native-to-managed call with no
  reverse-P/Invoke thread attach and no cooperative/preemptive transition — neither of which is
  available during startup or with the world suspended.
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

## Layout verification

Types that cross the GC/EE boundary must be laid out exactly like their C++ counterparts.
`GCInterfaceOffsets.h` is the single source of truth for those layouts, and it is consumed twice:

* `nativeaot/Runtime/GCInterfaceOffsetsVerify.cpp` expands it into `static_assert`s against
  `gcinterface.h`/`gcinterface.ee.h`, so the native build breaks if the C++ layout drifts.
* `src/GCInterfaceOffsets.cspp` is preprocessed by the native build into `GCInterfaceOffsets.cs`,
  a set of C# constants that `GCInterfaceLayout.Verify()` checks the managed structs against.

This mirrors the existing `AsmOffsets.h`/`AsmOffsets.cspp` mechanism used by
`System.Private.CoreLib`.
