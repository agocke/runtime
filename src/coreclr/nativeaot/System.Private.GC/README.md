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
| `Environment/GCEvent.Unix.cs` | `gc/unix/events.cpp` |
| `Environment/GCEvent.Windows.cs` | `gc/windows/gcenv.windows.cpp` (`GCEvent::Impl`) |
| `Environment/GCEnvSync.cs` | `env/gcenv.os.h` (`CLRCriticalSection`), `env/gcenv.sync.h` |
| `Environment/GCEnvSync.Unix.cs` | `src/native/minipal/mutex.c` (the pthread half) |
| `Environment/GCEnvSync.Windows.cs` | `src/native/minipal/mutex.c` (the CRITICAL_SECTION half) |
| `Environment/SyncTypes.Unix.cs` | the `<pthread.h>` / `<time.h>` types the two above name |
| `Environment/SyncTypes.Windows.cs` | the `<windows.h>` type the two above name |
| `Environment/SyncImports.Unix.cs` | the `<pthread.h>` / `<time.h>` entry points the two above call |
| `Environment/SyncImports.Windows.cs` | the `<windows.h>` entry points the two above call |
| `Environment/GCToOSInterface.cs` | `env/gcenv.os.h` (`GCToOSInterface`) |
| `Environment/GCToOSInterface.VirtualMemory.Unix.cs` | `gc/unix/gcenv.unix.cpp` (virtual memory), `env/gcenv.unix.inl` |
| `Environment/GCToOSInterface.VirtualMemory.Windows.cs` | `gc/windows/gcenv.windows.cpp` (virtual memory), `env/gcenv.windows.inl` |
| `Environment/GCToOSInterface.WriteWatch.Unix.cs` | `gc/unix/gcenv.unix.cpp` (write watch) |
| `Environment/GCToOSInterface.WriteWatch.Windows.cs` | `gc/windows/gcenv.windows.cpp` (write watch) |
| `Environment/GCToOSInterface.Thread.Unix.cs` | `gc/unix/gcenv.unix.cpp` (sleep and yield) |
| `Environment/GCToOSInterface.Thread.Windows.cs` | `gc/windows/gcenv.windows.cpp` (sleep and yield) |
| `Environment/GCToOSInterface.MemoryLimits.Unix.cs` | `gc/unix/gcenv.unix.cpp` (memory limits and cache sizing), `gc/unix/cgroup.cpp` |
| `Environment/GCToOSInterface.MemoryLimits.Windows.cs` | `gc/windows/gcenv.windows.cpp` (memory limits and cache sizing) |
| `Environment/GCToOSInterface.Timers.Unix.cs` | `gc/unix/gcenv.unix.cpp` (timers) |
| `Environment/GCToOSInterface.Timers.Windows.cs` | `gc/windows/gcenv.windows.cpp` (timers) |
| `Environment/GCToOSInterface.Processors.Unix.cs` | `gc/unix/gcenv.unix.cpp` (processor counts and identity) |
| `Environment/GCToOSInterface.Processors.Windows.cs` | `gc/windows/gcenv.windows.cpp` (processor counts and identity, `GroupProcNo`) |
| `Environment/GCToOSInterface.Imports.Unix.cs` | the `<sys/mman.h>` / `<sys/resource.h>` / `<time.h>` / `<sched.h>` / `<unistd.h>` / `<sys/sysctl.h>` / `<sys/sysinfo.h>` / `minipal/time.h` entry points the above call |
| `Environment/GCToOSInterface.Imports.Windows.cs` | the `<windows.h>` and `<psapi.h>` entry points the above call |
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

Virtual memory management is translated rather than forwarded. `VirtualReserve`,
`VirtualRelease`, `VirtualCommit`, `VirtualDecommit`, `VirtualReset`,
`VirtualReserveAndCommitLargePages`, `GetPageSize`, `GetVirtualMemoryLimit` and
`GetVirtualMemoryMaxAddress` are the statements of `gc/unix/gcenv.unix.cpp` and
`gc/windows/gcenv.windows.cpp`, calling `mmap`/`munmap`/`mprotect`/`madvise`/`getrlimit` and
`VirtualAlloc`/`VirtualFree`/`VirtualAllocExNuma` through `[RuntimeImport]` declarations of the
libc and Win32 entry points themselves. Two details do not come from a C header:

* The `<sys/mman.h>`, `<sys/resource.h>` and `<windows.h>` constants are written out per
  platform in the C# and checked against the real headers by `static_assert`s in
  `nativeaot/Runtime/gcenv.managed.cpp`, which is compiled for the target platform. A platform
  whose values differ from the ones the `#if` selects breaks the build rather than the process.
* `GetPageSize` calls `minipal_getpagesize` on Unix -- the same cached `sysconf` the C++
  `GCToOSInterface::Initialize` reads -- and returns Windows' fixed 4 KB constant otherwise,
  which is what the C++ `minipal_getpagesize` is there.

Write watching is translated too. On Windows, `SupportsWriteWatch` is the same feature probe the
C++ performs -- a `MEM_WRITE_WATCH` reservation of one allocation granularity, released again --
and `ResetWriteWatch` and `GetWriteWatch` call the Win32 functions of those names through
`[RuntimeImport]`, keeping the `WRITE_WATCH_FLAG_RESET` flag, the "zero means success" error
code rather than a `BOOL`, the in/out `ULONG_PTR` count and the assert that the granularity the
OS reports back is the page size. The allocation granularity comes from `GetSystemInfo` at the
point of use rather than from the cached `g_SystemInfo` that only the C++ `Initialize` fills; it
is the same machine constant. On Unix there is no write watch, so `SupportsWriteWatch` is a
constant `false` that reserves nothing and the other two only assert, exactly as the C++ does --
the collector uses software write watch there instead.

Events and locks are translated as well. `GCEvent` is the `GCEvent::Impl` of
`gc/unix/events.cpp` -- a condition variable, a mutex, and the manual-reset and state flags,
with the same predicate loop, the same monotonic deadline arithmetic, the same broadcast under
the mutex, and the same auto-reset clear in the waiter -- or, on Windows, the `GCEvent::Impl` of
`gc/windows/gcenv.windows.cpp`, which is a Win32 event handle and four calls. `CLRCriticalSection`
is the `minipal_mutex` of `src/native/minipal/mutex.c`: a recursive `pthread_mutex_t`, or a
`CRITICAL_SECTION`. All of them call `pthread_*`, `clock_gettime` and the Win32 event and
critical section functions through `[RuntimeImport]` declarations of those entry points, so a
wait or an `Enter` parks the calling thread in libc or the kernel without a GC mode transition,
which is what the collector's own threads need while the world is suspended. As with virtual
memory, the platform constants -- `PTHREAD_MUTEX_RECURSIVE`, `CLOCK_MONOTONIC`,
`CLOCK_UPTIME_RAW`, `ETIMEDOUT` -- and the sizes of the opaque pthread and `CRITICAL_SECTION`
blobs are written out per platform in the C# and asserted against the real headers by
`nativeaot/Runtime/gcenv.managed.cpp`. `struct timespec` is the one type the managed code writes
into rather than passing through, so its layout is asserted exactly, in the two variants the C#
selects between: two native-sized words, or the 64-bit `time_t` that musl uses on every
architecture. OpenBSD has explicit branches for its BSD mmap, rlimit, pthread, clock and errno
values rather than falling through to the Linux constants.

Sleeping and yielding are translated too, and they are the first two of the "thread and process"
methods to be. `Sleep` returns immediately for zero, splits the millisecond count into the
`struct timespec` the C++ builds, and retries `nanosleep` with the remaining interval the kernel
hands back for as long as the call fails with `EINTR` -- the loop that keeps a signal from
shortening the collector's backoff. `YieldThread` calls `sched_yield` once and asserts, ignoring
`switchCount` exactly as the C++ does; on Windows the two are `SleepEx(sleepMSec, FALSE)` guarded
by the same `sleepMSec > 0` test and a single `SwitchToThread()` whose result the C++ discards.
The one thing C# cannot spell is `errno`: it is a macro over a C thread-local reachable only
through a per-C-library accessor function, so the port declares that accessor as an import
and dereferences what it returns, selecting `__error` on Apple and FreeBSD, `__errno` on bionic
and OpenBSD, and `__errno_location` on glibc and musl. Which one is selected is asserted against
the native platform defines in `gcenv.managed.cpp`, together with `EINTR` and the existence and
shape of `nanosleep` and `sched_yield`; the bionic case is keyed on a `TARGET_BIONIC` define that
covers both the `android` and the `linux-bionic` runtime identifiers, because the native build
labels the latter Linux.

The memory limits and the cache sizing are translated as well. `GetPhysicalMemoryLimit`,
`GetMemoryStatus` and `GetCacheSizePerLogicalCpu` are the statements of `gc/unix/gcenv.unix.cpp`
and `gc/windows/gcenv.windows.cpp`, together with the helpers under them:
`GetRestrictedPhysicalMemoryLimit`, `GetPhysicalMemoryUsed`, `GetAvailablePhysicalMemory`,
`GetAvailablePageFile` and the four `GetLogicalProcessorCacheSizeFrom*` functions on Unix, and
`GetRestrictedPhysicalMemoryLimit`, `GetLPI` and `GetLogicalProcessorCacheSizeFromOS` on Windows.
They call `sysconf`, `getrlimit`, `sysinfo`, `sysctl`/`sysctlbyname`/`sysctlnametomib`,
`GlobalMemoryStatusEx`, `IsProcessInJob`, `QueryInformationJobObject`,
`GetLogicalProcessorInformation` and `K32GetProcessMemoryInfo` through `[RuntimeImport]`
declarations of the entry points themselves. Every sentinel and every saturation of the C++
survives: the `0x7FFFFFFF00000000` above which a cgroup v1 limit means "unrestricted", the
`SIZE_T_MAX` clamp of a limit that does not fit a `size_t`, the sticky flag that stops rereading
`/proc/meminfo` once it has failed once, the all-`float` load percentage that can exceed 100 when
a process is over its limit, and the Windows quirk of reading `ullAvailPhys` into `total_physical`
while interrogating a job object. The `_SC_*` names, the `struct sysinfo` and `struct xsw_usage`
and `struct xswdev` layouts, the `CTL_VM`/`VM_SWAPUSAGE` numbers, and the Win32 job object,
psapi and logical-processor layouts are written out per platform in the C# and asserted against
the real headers by `gcenv.managed.cpp` -- including `#error`s on the presence or absence of
`_SC_AVPHYS_PAGES` and the `_SC_LEVEL*` family, so a C library that grows or loses one breaks
the build rather than silently changing the answer.

The three timers follow, and they are the smallest submodule so far. On Unix
`QueryPerformanceCounter`, `QueryPerformanceFrequency` and `GetLowPrecisionTimeStamp` are one
call each into `src/native/minipal/time.h`, which is a static library already on every NativeAOT
link line, so the port imports `minipal_hires_ticks`, `minipal_hires_tick_frequency` and
`minipal_lowres_ticks` directly and retains no shim at all. Translating `time.c` itself was
rejected: it selects between `clock_gettime_nsec_np`, `CLOCK_MONOTONIC_COARSE` and
`CLOCK_MONOTONIC` on configure-time probes that have no managed spelling, and forking that
selection would be a behavior change rather than a translation. On Windows the C++ calls Win32
directly, so the port does too -- `QueryPerformanceCounter`, `QueryPerformanceFrequency` and
`QueryUnbiasedInterruptTime`, each asserting on a zero return and then returning the value the
failed call left behind rather than a sentinel of its own, and the last of them divided by the
same `TicksPerMillisecond` of 10000. The output local is declared uninitialized, as the C++
declares it, so nothing in the port forces a value onto that unreachable path; because the
assembly does not set `SkipLocalsInit`, the runtime zeroes it anyway, which is the one residual
difference from the C++, where the value would be whatever the stack held. `LARGE_INTEGER` is spelled `long` because it is an eight-byte
union whose `QuadPart` -- the only member the C++ reads -- necessarily begins at offset zero;
`gcenv.managed.cpp` asserts that size, that member's size, and the existence and return width of
all six entry points.

The processor counts and identity come next, and they are the first submodule whose C++ reads
state that `GCToOSInterface::Initialize` computes, so it is the first to keep explicit state
shims rather than recompute anything. On Unix `GetCurrentProcessId` is `getpid`, and
`GetCurrentProcessorNumber` and `CanGetCurrentProcessorNumber` are the two arms of
`HAVE_SCHED_GETCPU`: the configure probe of `gc/unix/configure.cmake` runs on every NativeAOT
Unix target and answers yes on Linux with glibc, musl and bionic and no on Apple, FreeBSD and
OpenBSD, so the port spells it `#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD` and
`gcenv.managed.cpp` `static_assert`s the value of `HAVE_SCHED_GETCPU` from the generated
`config.gc.h` against that same shape on both arms. Where the probe says no, the port keeps the
C++ `assert(false); return 0;` and reports `false`, so a platform that gains `sched_getcpu`
without gaining the `#if` fails closed exactly as the C++ does.
`GetCurrentThreadIdForLogging` is the one Unix method that cannot call its C++ target: unlike the
minipal timer entry points, `minipal_get_current_thread_id` is a `static inline` over a
`_Thread_local` cache in `src/native/minipal/thread.h` and therefore has no symbol to import, so
it keeps the leaf `ManagedGC_Unix_GetCurrentThreadId`.

On Windows all three identity methods are direct Win32 calls, `CanGetCurrentProcessorNumber` is
the C++ `return true`, and `GetCurrentProcessorNumber` translates the `GroupProcNo` packing --
`(group << 6) | procIndex`, with the two `assert`s on the group and index widths -- alongside the
`PROCESSOR_NUMBER` layout, all of which `gcenv.managed.cpp` checks against `<windows.h>`.

`GetTotalProcessorCount` and `GetMaxProcessorCount` read `g_totalCpuCount` and
`g_processAffinitySet`, and on Windows also the CPU group state, none of which the managed side
owns yet: `Initialize` is still native, and recomputing the numbers here would give them a
different lifetime than the C++ gives them. The port therefore reaches the existing state through
the narrowest possible accessors -- `ManagedGC_Unix_GetTotalCpuCount` and
`ManagedGC_Unix_GetProcessAffinitySet` on Unix, and `ManagedGC_Windows_GetTotalCpuCount`,
`ManagedGC_Windows_GetCpuGroupProcessorCount`, `ManagedGC_Windows_GetSystemInfoProcessorCount`
and `ManagedGC_Windows_GetProcessAffinitySet` on Windows. The Windows total is a `uint32_t*`
rather than a value because the C++ body caches into `g_totalCpuCount` on first call and the port
must perform that same write; the other three are values because the C++ only reads them. All of
them are deleted with the initialization, affinity and CPU-group submodules. These two C++
bodies are also the first the port replaces that must stay compiled: `PalUnix.cpp` and
`PalMinWin.cpp` call `GetTotalProcessorCount` and `gcconfig.cpp` calls `GetMaxProcessorCount`,
and all three are in the managed runtime archive, so only the four identity methods are excluded
by `FEATURE_MANAGED_GC`.

Four things that read or write a file cannot be translated without allocating, so they stay
native for now, as the narrowest possible leaves: `ManagedGC_CGroup_GetPhysicalMemoryLimit` and
`ManagedGC_Unix_GetPhysicalMemoryUsed` wrap the `CGroup` class of `gc/unix/cgroup.cpp`, which is
in an anonymous namespace, and `ManagedGC_Unix_ReadMemoryValueFromFile`,
`ManagedGC_Unix_ReadMemAvailable` and `ManagedGC_Unix_GetCurrentVirtualMemorySize` wrap the
`static` `/sys` and `/proc` readers of `gcenv.unix.cpp`. `ManagedGC_Unix_GetProcessAffinitySet`
hands back the `g_processAffinitySet` that `GCToOSInterface::Initialize` fills, which the
affinity submodule owns. All six are deleted with the cgroup and affinity submodules; Windows
retains nothing.

The managed runtime archive no longer compiles the Unix `events.cpp`; on Windows,
`FEATURE_MANAGED_GC` excludes the `GCEvent::Impl` section of `gcenv.windows.cpp`, and on both
platforms it excludes the `Sleep` and `YieldThread` section of `gcenv.unix.cpp` and
`gcenv.windows.cpp`, the memory limit and cache sizing sections of `gcenv.unix.cpp`,
`gcenv.windows.cpp` and `gc/unix/cgroup.cpp`, the timer section of `gcenv.unix.cpp` and
`gcenv.windows.cpp`, and the four processor identity methods of `gcenv.unix.cpp` and
`gcenv.windows.cpp`. Those three files remain in the archive only for
the `GCToOSInterface` services that have not been translated yet, and for the six leaf helpers
above; the workstation and server GC archives still compile every one of those bodies
unchanged.

Two C++ shapes are preserved rather than corrected, because this is a translation: `CloseEvent`
releases the operating system object but neither frees the Impl nor clears the pimpl pointer, so
`IsValid()` keeps reporting true afterwards -- a `GCEvent` deliberately has no destructor, see
[dotnet/runtime#7919](https://github.com/dotnet/runtime/issues/7919) -- and on Windows a failed
`CreateEvent` returns `NULL`, which the C++ `IsValid()` does not recognize as a failure because
it compares against `INVALID_HANDLE_VALUE`.

Everything else that reaches the operating system is declared with the C++ signature and
forwarded, for now, to a one-line shim in `nativeaot/Runtime/gcenv.managed.cpp` that calls the
existing C++ `GCToOSInterface` in `gc/unix/gcenv.unix.cpp` or `gc/windows/gcenv.windows.cpp`.
Those shims are the whole retained-native surface of this layer:

* one per remaining `GCToOSInterface` method (`ManagedGC_OS_*`) -- initialization and shutdown,
  thread affinity and priority, the ideal-processor pair, NUMA and CPU groups, the mapping of a
  heap to a processor, the platform-specific affinity range entry parser, and the debug break;
* the six Unix leaves the memory limit port still needs -- `ManagedGC_CGroup_*` and
  `ManagedGC_Unix_*` -- which are described above and are deleted with the cgroup and affinity
  submodules;
* the state accessors the processor count port needs -- `ManagedGC_Unix_GetCurrentThreadId`,
  `ManagedGC_Unix_GetTotalCpuCount` and the four `ManagedGC_Windows_*` above -- which are deleted
  with the initialization, affinity and CPU-group submodules;
* `ManagedGC_NUMA_BindMemoryPolicy`, which is the `mbind` half of `VirtualCommitInner`
  verbatim. It reads `g_numaAvailable` and `g_highestNumaNode` and calls `BindMemoryPolicy`,
  all of which belong to `gc/unix/numasupport.cpp`, so it is deleted with the NUMA submodule
  rather than with virtual memory;
* `ManagedGC_AllocZeroed` / `ManagedGC_Free`, which stand in for the `new (nothrow)`
  allocations of the environment layer -- the `uintptr_t[]` of `AffinitySet::Initialize`, the
  `GCEvent::Impl` of the event ports, the `minipal_mutex` that the C++ `CLRCriticalSection`
  embeds by value, and the `SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]` of the Windows `GetLPI` --
  and which are the only heap allocation the managed GC performs.

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
  embedding a `minipal_mutex` by value, and `Initialize` and `Destroy` own that allocation. The
  embedded object is a `pthread_mutex_t` or a `CRITICAL_SECTION`, whose size differs per
  operating system, and `GCInterfaceOffsets.h` carries one value per pointer size rather than one
  per platform. Nothing passes a `CLRCriticalSection` across a boundary, so no layout depends on
  the difference.
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

`GCVirtualMemoryTests` and `GCWriteWatchTests` run the virtual memory and write watch ports
themselves. The shipping bodies are the code under test; only the libc/Win32 declarations
underneath them are substituted, by `tests/GCToOSInterface.Imports.*.TestHost.cs`, which declares
the same private methods as ordinary P/Invokes -- something the GC itself must never do --
forwards each call to the real kernel and records its arguments. The tests therefore check the
flag translation, the alignment over-allocation and trimming, and the failure paths exactly, and
they also run the sequences the collector performs: reserve, commit, write, reset, decommit,
re-commit and release, on raw pages, without the managed heap; and, on Windows, reserve with
write watching, dirty individual pages and read exactly those pages back, with and without the
reset flag. On Unix the write watch tests pin the platform behavior that makes the collector use
software write watch: an unsupported answer that reserves nothing. The expected flag values are
written out in the test rather than read from the constants of the port, so a wrong constant
fails a test instead of being confirmed by it.

`GCEventTests` and `GCCriticalSectionTests` do the same for the event and lock ports, over
`tests/SyncImports.*.TestHost.cs`. Because the substitutes forward to the real pthreads or the
real Win32 objects, the tests are behavior tests and not just call-shape tests: manual versus
auto reset, the initial state, a timeout that actually elapses, a blocking wait released by
another thread, a manual event releasing every waiter against an auto event releasing exactly
one per `Set`, a two-event ping-pong of two thousand round trips that loses no signal, twenty
thousand set/reset pairs racing three pollers, the recursive lock's nesting, and four threads
contending for it without losing an update. The recorded calls pin the translation itself: the
mutex attribute is `PTHREAD_MUTEX_RECURSIVE`, the condition variable is created against
`CLOCK_MONOTONIC` and the deadline read from it, the deadline of a longer wait lands the right
distance away -- which is what checks the nanosecond carry -- `Set` broadcasts under the mutex
and `Reset` does not, a wait satisfied immediately never touches the condition variable, and the
Impl comes from a single nothrow allocation. The failure paths -- the allocation, the mutex and
the condition variable each failing to initialize -- are driven by injection, and are compiled
only into a build with asserts disabled, because the C++ asserts on them too.

`GCSleepYieldTests` runs the sleep and yield ports over the same substituted imports. The
millisecond split is checked against the `timespec` the port actually wrote for a table of
intervals up to `uint.MaxValue`, which is what pins the second/nanosecond arithmetic; the `EINTR`
loop is checked by injecting interruptions and asserting that each retry passes the *remaining*
interval the previous call reported, so a port that retried with the original request would fail;
and a failure with any other `errno` is checked not to retry at all. `YieldThread` is checked to
call `sched_yield` -- or `SwitchToThread` -- exactly once per call whatever `switchCount` is, and
on Windows `SleepEx` is checked to receive the interval unchanged with `bAlertable` false, and
not to be called at all for zero. Everything except one deliberately coarse lower bound per
platform is driven by injection rather than by the clock, so no test waits on real time to
decide.

`GCTimerTests` covers the three timer methods. On Unix each one is a single call, so what the
tests pin is that it is that call, made once per invocation and cached nowhere, and that the
value returns unchanged for the whole `int64_t` range -- including the negative counts that check
the signed-to-unsigned reinterpretation of `GetLowPrecisionTimeStamp`, which a conversion rather
than a cast would clamp. On Windows the same range is checked through the `QuadPart` the two
`LARGE_INTEGER` calls fill, the truncating division by `TicksPerMillisecond` is checked across
its boundaries up to `ulong.MaxValue`, and the three failure paths, where the C++ asserts
and then returns whatever the failed call left behind, are compiled only into a build with
asserts disabled, as the event and lock failure tests are.

`GCProcessorTests` covers the processor counts and identity. The identity methods are one call
each, so what the tests pin is that it is that call, made once, with the result widened rather
than converted -- a `getpid` of -1 must come back as `uint.MaxValue` and a thread id with the top
bit set must survive -- and, where the machine can answer, that the uninjected value is the one
the host reports. `CanGetCurrentProcessorNumber` and the `sched_getcpu` arm of
`GetCurrentProcessorNumber` are compiled per platform, so the Unix half of the file has one
section for each answer of `HAVE_SCHED_GETCPU` and the arm that asserts is, like the timer
failures, compiled only into a build with asserts disabled. The Windows `GroupProcNo` packing is
checked over the corners of both fields, up to the `(0x3ff, 0x3f)` that fills the `uint16_t`. The
counts are checked against the state the shims stand in for: that `GetMaxProcessorCount` is the
capacity of the affinity set rather than its population, and on Windows that
`GetTotalProcessorCount` caches into `g_totalCpuCount` on first call, short-circuits on every
later call, picks the CPU group total or the `SYSTEM_INFO` total according to
`CanEnableGCCPUGroups`, and keeps asking while the answer is zero.

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
