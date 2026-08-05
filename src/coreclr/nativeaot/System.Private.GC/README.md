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
| `GCEvents.cs` | `gceventstatus.h`, expanded `gcevents.h` event helpers |
| `GCEventSerializer.cs` | `gcevent_serializers.h` |
| `GCEventStatus.cs` | `gceventstatus.h`, `gceventstatus.cpp` |
| `GCDesc.cs` | descriptor encoding and MethodTable lookup from `gcdesc.h` |
| `GCPriv.cs` | dependency-free leaf records from `gcpriv.h` |
| `GCRecord.cs` | schema and non-diagnostic helpers from `gcrecord.h`, plus `gc_reason` from `gc.h` |
| `HandleTableConstants.cs` | `handletableconstants.h` |
| `HandleTable.cs` | `handletable.cpp` (lifecycle, creation, destruction, metadata, and write-barrier subset) |
| `HandleTableCache.cs` | `handletablecache.cpp` |
| `HandleTableCore.cs` | `handletablecore.cpp` (segment lifecycle and handle-to-segment mapping) |
| `ObjectHandle.cs` | `objecthandle.h`, `objecthandle.cpp` (map, bucket, initialization, and dependent-handle subset) |
| `HandleTableStructs.cs` | `handletablepriv.h` (segment header, segment, type cache) |
| `IntroSort.cs` | `introsort.h` |
| `Interface/GCInterfaceEnums.cs` | `gcinterface.h`, `gcinterface.ee.h` (enums) |
| `Interface/GCInterfaceStructs.cs` | `gcinterface.h`, `gcinterface.ee.h` (shared structs) |
| `Interface/GCInterfaceVtables.cs` | `gcinterface.h`, `gcinterface.ee.h`, `gc.h` (abstract classes) |
| `Interface/GCInterfaceDac.cs` | `gcinterface.dac.h` (`GcDacVars` and the DAC analogue types) |
| `Interface/GCInterfaceLayout.cs` | layout check against `GCInterfaceOffsets.h` |
| `Interface/GCToEEInterface.cs` | `gcenv.ee.standalone.inl` |
| `GCCommon.cs` | dependency-closed helpers from `gccommon.cpp` |
| `GCConfig.cs` | `gcconfig.h`, `gcconfig.cpp` |
| `ManagedGCEntryPoints.cs` | `gcload.cpp` (`GC_VersionInfo`, `GC_Initialize`) |
| `Environment/GCEnv.Base.cs` | `env/gcenv.base.h`, plus `ParseIndexOrRange` of `gcconfig.cpp` |
| `Environment/GCEnv.MemoryBarrierProcessWide.cs` | `src/native/minipal/memorybarrierprocesswide.h` |
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
| `Environment/GCToOSInterface.Processors.Unix.cs` | `gc/unix/gcenv.unix.cpp` (processor counts and identity, affinity, NUMA) |
| `Environment/GCToOSInterface.Processors.Windows.cs` | `gc/windows/gcenv.windows.cpp` (processor counts and identity, affinity, NUMA, CPU groups, `GroupProcNo`) |
| `Environment/GCToOSInterface.Imports.Unix.cs` | the `<sys/mman.h>` / `<sys/resource.h>` / `<time.h>` / `<sched.h>` / `<unistd.h>` / `<sys/sysctl.h>` / `<sys/sysinfo.h>` / `minipal/time.h` entry points the above call |
| `Environment/GCToOSInterface.Imports.Windows.cs` | the `<windows.h>` and `<psapi.h>` entry points the above call |
| `SoftwareWriteWatch.cs` | `softwarewritewatch.h`, `softwarewritewatch.cpp` |
| `GCScan.cs` | dependency-closed parts of `gcscan.cpp` |
| `GCHeapMemory.cs` | `gcenv.ee.cpp` write-barrier publication, `card_table.cpp` (tables only) |
| `MarkPhase.cs` | dependency-closed pinned-plug queue helpers from `mark_phase.cpp`, `gcinternal.h` |
| `GCAllocation.cs` | dependency-closed WKS `USE_REGIONS` heap allocation state, allocation-context creation/callback plumbing, free-list/segment-end orchestration and fitting, `allocate_more_space` / deferred-operation state machines, refill-transition, and free-object helpers from `allocation.cpp`, `sweep.cpp`, and `gcinternal.h` |
| `GCMemory.cs` | dependency-closed WKS region memory helpers from `memory.cpp` |
| `GCRegionsSegments.cs` | dependency-closed WKS `USE_REGIONS` mapping and region-table helpers from `regions_segments.cpp`, `plan_phase.cpp`, `background.cpp`, `diagnostics.cpp`, and `gc.cpp` |
| `GCWriteBarrier.cs` | WKS `USE_REGIONS` write-barrier helpers from `gc.cpp` |
| `ManagedGCHeap.cs` | `gcinterface.h` `IGCHeap` (non-collecting subset) |
| `ManagedGCHandleManager.cs` | `objecthandle.cpp`, `gchandletable.cpp` (single-table subset) |

For `gcload.cpp`, the part `Runtime.ManagedGC` actually reaches is now complete: the managed
entry points preserve the loader protocol order for output clearing, incoming `IGCToCLR`
recording, interface-layout verification, managed `GCConfig` initialization, and
handle-manager/heap creation with OOM returns. The direct ABI/version and failure-path coverage
for this lives in `tests/ManagedGCEntryPointsTests.cs`.

`gcinterface.dac.h` is translated, including the `dac_generation` and `dac_gc_heap` views
generated from `dac_generation_fields.h` and `dac_gcheap_fields.h`. Pointer-sized arrays use
conditional primitive fixed buffers because C# fixed buffers do not accept `nuint`; arrays of
translated structures are represented by contiguous numbered fields. The handle-table analogues
are present with the constants and packed segment schema they depend on. Nothing populates a
`GcDacVars` yet:
`PopulateDacVars` publishes the addresses of the collector's data structures, which this heap
does not have. The managed selector therefore leaves its DAC interface version zero, making a
DAC reject this collector as unsupported until those structures are available.

The first real handle-table slices establish the schema and segment lifecycle:
`HandleTableConstants.cs` translates the size, mask, block, clump, and cache arithmetic of
`handletableconstants.h`; `HandleTableStructs.cs` translates the byte-packed
`_TableSegmentHeader`, the 64-KiB `TableSegment`, the fixed `HandleTable` header, and
`HandleTypeCache`; and
`HandleTableCore.cs` reserves aligned segments, commits and initializes their headers, releases
them, maps handle addresses back to their segment headers, maintains the native byte-sized block
lock counts, moves blocks from the segment free list into circular per-type chains, and allocates
handle slots from their free masks and existing type chains. It also removes empty unlocked
blocks from those chains, preserving free-list order and recursively reclaiming parallel user-data
blocks. It now also frees sorted handle batches across masks and blocks, clears per-handle
parallel user data, updates free counts without crediting duplicate frees, and returns newly
empty blocks to the segment free list. Chain resorting rebuilds all type chains and the free list
in address order, completes deferred scavenging, and tracks the trailing empty range so whole
unused pages can be decommitted. The public block-insertion path consults the native
`HandleTable.rgTypeFlags` prefix and allocates, links, and locks parallel user-data blocks for
types marked `HNDF_EXTRAINFO`. The shared `GCInterfaceOffsets.h` table pins the segment and cache
layouts plus the load-bearing `HandleTable.rgTypeFlags` prefix and flag values against the native
headers, and the managed startup verifier checks the C# definitions against the generated values.
`HandleTable.cs` now allocates and initializes a table, its first segment, lock, type flags, and
trailing main caches, and destroys the complete segment list. `HandleTableCache.cs` translates
the reserve bank, free bank, quick-cache, cache-miss, and full/quick rebalance paths. Its
low-water path is backed by the translated bulk table allocation entrypoint, and its high-water
path uses the native free-order comparison and prepared bulk free path. Handle type and owning
table lookup, table containment, extra-info get/set/compare-exchange, and cache-aware handle
counting are also translated. Typed and unknown-type single destruction use the cache, while
unprepared bulk free copies, sorts, clears, and frees handles in native-sized chunks with an
unmanaged large scratch buffer when available. `GetConvertedGeneration`,
`HndWriteBarrierWorker`, `HndAssignHandle`, `HndAssignHandleGC`,
`HndInterlockedCompareExchangeHandle`, `HndFirstAssignHandle`, and `HndCreateHandle` publish
object references with the same clump-age barriers, conditional event logging, and
extra-info-before-referent ordering as the C++ table. `ManagedGCHandleManager` now uses this
translated table for the running NativeAOT heap rather than maintaining a separate flat slot
allocator. The current bootstrap uses one global table; per-heap table selection remains tied to
the later multi-heap collector work. The native `HandleTableMap`/`HandleTableBucket` shapes and
their initialization, removal, destruction, and allocation-failure cleanup are translated
directly; the one-heap collector makes the current bucket contain one table.
All-table handle counting and the variable-handle type helpers also operate over this translated
map and extra-info storage. `HndNotifyGcCycleComplete` currently has its retail no-op behavior;
the checked-build scan-statistics logging arrives with handle scanning.

The GC history schema translates `gcrecord.h`: the
generation and condition condemn-reason enums, their native two-bit/one-bit packed tuning
record, the ten-field `gc_generation_data` event payload, `maxgen_size_increase`, and the
per-heap and global mechanism histories. It also carries the `gc_reason` enum those records use
from `gc.h`. The shared offsets table verifies the public record layouts and every enum value
against the real WKS and SVR headers; direct tests cover the private tuning record's size, native
OR-based bit packing, most-significant-bit mechanism encoding, and mechanism flags. The
string-based diagnostic `print` bodies remain with the later tracing work.

`GCPriv.cs` starts the main collector schema with the dependency-free `static_data`,
`recorded_generation_info`, `last_recorded_gc_info`, `etw_opt_info`, allocation-wait records,
`no_gc_region_info`, and their related enums. These retain the native pointer-sized fields and
names so the later dynamic-tuning, diagnostics, generation, and no-GC-region ports can embed them
without an adapter. Native arrays are contiguous numbered fields, and the C++ `bool` snapshot
flags are bytes so their one-byte ABI is explicit. Their complete layouts and enum values are
checked against both workstation and server GC headers and again by the managed startup verifier
and foundation tests. The same file also carries the dependency-free collector enums for LOH
compaction, latency and tuning, object-heap identity, memory state, allocation state, and
multi-segment-lock entry. The first plan/relocation records are present too: `plug`, `pair`,
`plug_and_pair`, `plug_and_reloc`, the overlaid `plug_and_gap`, `gap_reloc_pair`,
the forced-alignment `aligned_plug_and_gap`, `loh_obj_and_pad`, and `loh_padding_obj`.
It also carries the native test-only `gc_rand` linear congruential generator and its adjacent
spin, cross-generation-reference, and target-width mark-stack constants.
The `bk` and `sorted_table` storage schema is present as well, including the leading old-slot
link, bucket offset, initialization, and maximum-pointer sentinel. Its allocation-free binary
lookup, sorted insertion, containing-interval removal, and clear operations run over
caller-provided storage. Creation and growth use the managed runtime's unmanaged allocation
surface, preserve old arrays until explicit reclamation, and release current/queued arrays in
the same order as native.
The dependency-free portion of `mark` from `gcinternal.h` is translated too: its complete
short-plug schema, native `BOOL` bit predicates, pointer accessors, and allocation-free
gap/relocation-pair swaps are present. `SHORT_PLUGS` is unconditional, while
`COLLECTIBLE_CLASS` remains gated as it is natively (disabled for NativeAOT). `recover_plug_info`
is deferred until the compaction settings and diagnostics slices are available.
`MarkPhase.cs` begins `mark_phase.cpp` with the dependency-closed pinned-plug queue setup:
queue reset/dequeue and boundary helpers, mark-stack setup, growth and overflow reset, saved
post-plug recovery, and allocation-limit clipping at the oldest pin. Mark-stack growth keeps the
native unmanaged ownership transfer: it copies the active entries only after allocating a
replacement, then releases the old block. It also carries the NativeAOT `MethodTable`/
`ObjectLayout` view of the object prefix: pointer flags/accessors, `CObjectHeader` marked and
special-bit operations, and the `clear_special_bits`/`set_special_bits`, `method_table`, `contain_pointers`, and
short-plug-size helpers that enqueue/save require. Its allocation-free
`go_through_object_nostart` leaf reads normal and repeating `GCDesc` maps in the native order and
passes each reference slot to a direct managed function pointer; it does not introduce a delegate,
allocation, or reverse P/Invoke transition. It does not mark references or run a collection.
Pinned-plug enqueue/save now preserve both copies of the overwritten gap records, special header
bits, short-object reference bits, and the unconditional `SHORT_PLUGS` padded-header helpers.
`record_interesting_data_point` remains mechanically omitted: `Runtime.ManagedGC` does not
compile the C++ mark phase and `System.Private.GC` does not define `GC_CONFIG_DRIVEN`, so it has
no native diagnostic storage to update. The `_DEBUG && VERIFY_HEAP`
`verify_pinned_queue_p = TRUE` assignment in `save_post_plug_info` remains deferred with
`verify_pins_with_post_plug_info` and relocate-compact verification state. The next mark-phase
slice translates the foreground marking leaves: mark-bit query and covered-range clearing retain
the biased region mark-array addressing and partial-first-word behavior, while `gc_mark1` and
the active WKS `USE_REGIONS` `gc_mark` retain object-header marking, half-open bounds, and
region-generation rejection. It also now carries the active WKS `USE_REGIONS` 16-slot
`MARK_PHASE_PREFETCH` queue, mark-stack slot tags and untagging, object-size arithmetic,
resumable `GCDesc` traversal, and overflow-address extrema. The queue preserves its native
rotation and delayed marking; the native prefetch instruction remains a performance-only gap
because no cross-platform managed primitive is available. Its condemned-generation overload is
intentionally compiled only for `USE_REGIONS`; the non-region `gc_low`/`gc_high` branch remains
deferred with that active-collection state. These leaves are not routed from collection entrypoints.
The next active WKS `USE_REGIONS` prerequisites are present too: the fixed-capacity
`m_boundary` and full-GC-only `m_boundary_fullgc` leaves preserve inclusive list ends, exhausted
list cursors, and `slow`/`shigh` extrema. The promoted-byte overloads record by the biased basic
region index with native unsigned overflow, while the debug-only WKS global recording and reset
remain guarded exactly as native. The `survived_per_region` and
`old_card_survived_per_region` storage pointers are now WKS static state, like their native
`PER_HEAP_FIELD_SINGLE_GC` definitions. `gc_mechanisms.first_init` and
`init_mechanisms` retain the condemned-generation defaults, debug latency-mode override, and
full-GC decision input. Although the managed project does not define a C# `FEATURE_LOH_COMPACTION`
symbol, native `gcpriv.h` enables that feature unconditionally; the managed lifecycle therefore
also initializes the native LOH-compaction request state from `GCConfig`, preserves its
default/once mode, and exposes it through the existing heap slots. Startup initializes this
state and the static mark queue in the common `ManagedGCHeap.Initialize` path, before the
region-specific branch, without routing collection. Collection setup accepts caller-owned
mark-list and counter storage, preserves the full-versus-partial list-end rule, and clears both
counter spans before resetting extrema. It rejects null or zero-length list storage with a false
result after resetting all mark state (debug-asserting that disabled state), so it cannot publish
a writable empty list. The native `g_mark_list`,
`g_mark_list_piece`, sizing policy, and growth/allocation ownership remain unported because
they depend on global planning and multi-heap state; this port deliberately does not allocate a
substitute. `mark_object_simple1`, `mark_object`, `drain_mark_queue`, and `mark_through_object`
remain unrouted. Background marking remains deferred.
The adjacent `card_table_info` schema and its dependency-free helpers are translated too. Its
DAC-compatible `recount`/`size`/`next_card_table` prefix is followed by the card, brick, and
unconditional card-bundle pointers; `mark_array` follows `BACKGROUND_GC` and is absent on WASM.
The native-width `gib`, brick/card alignment, card-word/bit, card-bundle-word/bit, and
pointer-to-card arithmetic preserve unsigned wrapping and division. `GC_PAGE_SIZE`, card-word
and card-bundle-word widths, target-width card size, and card-bundle size join the card-bundle
thresholds, decommit, and 64-bit tuning constants pinned with the layout.
Card-bundle/card-word conversion, translated bundle-table skewing, and card/brick table sizing
also preserve the native unsigned address arithmetic and half-open range coverage.
The card-table pointer accessors alias the `card_table_info` record immediately before the card
words, including its DAC-visible prefix and optional background mark array, and translated card
tables retain native zero-based word indexing.
For background GC builds, mark-bit pitch, word width/size, address alignment, bit/word indexing,
address reconstruction, and mark-array sizing now preserve the native target-width arithmetic.
The adjacent heap-segment iteration helpers skip read-only segments outside the heap's current
address range, preserve half-open segment address tests, and select the native generation
iteration bounds for region and non-region collectors.
The first allocator record, `alloc_list`, carries the 64-bit doubly-linked free-list prefix,
the common head/tail/damage state, and pointer-based ref accessors that preserve the native
reference-return behavior without introducing managed references. Its layout follows the
`BACKGROUND_GC`, `TARGET_WASM`, and diagnostic `FL_VERIFICATION` feature combinations.
The adjacent non-WASM `etw_bucket_info` event record and its replacing `set` operation are
translated too. The dependency-free core of the `allocator` class that owns those free lists is
translated with them: its private `first_bucket_bits`/`num_buckets`/`first_bucket`/`buckets`/
`gen_number` schema, parameterized construction and explicit young-generation initialization,
bucket counting, `first_suitable_bucket` size-to-bucket mapping (over the existing `BitScanReverse`
wrappers), `first_bucket_size`, `alloc_list_of`, the damage-count lookup, the pointer-based
head/tail/`added_` ref accessors, `clear`, `discard_if_no_fit_p`, and the non-WASM 64-bit
`is_doubly_linked_p` generation predicate. Since every native member is private, the shared table
pins only the allocator's size and alignment under the same `FL_VERIFICATION`/`TARGET_WASM`
combinations as its embedded `alloc_list`, while the managed tests pin the field order and every
accessor directly. The native default constructor is an explicit pointer initializer because C#
does not run struct constructors for embedded fields or storage carved from unmanaged memory.
The per-generation `dynamic_data` schema and its dependency-free `dd_*` accessors are translated
too. The native class has no constructor, so zero initialization matches its default, and its
fields are public, so the table pins each field's offset explicitly rather than only the size.
`padding_size` is always present because `SHORT_PLUGS` is unconditional; `num_npinned_plugs` and
the one-pointer shift of every later field follow `RESPECT_LARGE_ALIGNMENT || FEATURE_STRUCTALIGN`,
which reduces to the GC's `FEATURE_64BIT_ALIGNMENT` (`TARGET_ARM || TARGET_WASM`) since
`FEATURE_STRUCTALIGN` is never defined here, so the offsets table and the managed struct both gate
that field on it. Every native `dd_*` accessor -- the direct fields, the ones that forward through
`sdata`, and the doubling-and-capping `dd_v_fragmentation_burden_limit` -- is a static
ref-returning (or value-returning) helper taking a `dynamic_data*`, preserving the native
reference-return API without a managed reference to collector state.

The per-heap `generation` schema is translated on top of those `allocator` and `dynamic_data`
cores. Its `allocation_context` is a native `alloc_context`, which derives from `gc_alloc_context`
and adds no fields, so the port reuses the existing `gc_alloc_context` layout for it instead of a
distinct type. The dependency-closed `heap_segment` and region-only `generation_region_info`
schemas are translated alongside it. The segment preserves the region/non-region tail, the
server-only heap and decommit branches, the debug non-region saved fields, native one-byte
`swept_in_plan_p`, flags and age constants, and its dependency-free accessors; `gc_heap` remains
an opaque unmanaged declaration because this slice only stores its pointer. `region_free_list` now
has its native bookkeeping layout and the dependency-closed list-management core from
`region_free_list.cpp` (reset/add/unlink/transfer/aging/sort plus region-size accounting over
`heap_segment`). A minimal `region_allocator` prefix from `gcpriv.h` is now present too, through
`region_alignment` and `large_region_alignment` fields and their getters, with
`gc_heap.global_region_allocator` as its state carrier. It also carries
`LARGE_REGION_FACTOR`, `region_alloc_free_bit`, `allocate_direction`, and the dependency-closed
alignment/bit-decoding helpers (`align_region_up`, `align_region_down`, `is_region_aligned`,
`is_unit_memory_free`, `get_num_units`) that do not depend on allocator reservation or lock state.
The next dependency-closed schema slice extends the exact native field order through
`region_allocator_lock`, the four `region_map_*` pointers, and the used-free-unit counters, with
the minimal `GCSpinLock` schema/constructor-sentinel helper needed to carry that field. The
allocator spin-lock enter/leave loop is translated too, preserving the native `-1` free / `0`
held encoding, the compare-exchange acquire loop, debug owner sentinel/current-thread recording,
and the release store used to publish the unlocked state. The pure address/index arithmetic
helpers `region_address_of` and `region_map_index_of` are translated too.
The allocator's `init` now aligns the reserved range, initializes the map bounds and free-unit
counts in native order, allocates the zeroed `uint32_t` region map through the managed GC's
native nothrow allocation shim, and writes the returned lowest/highest bounds only after that map
allocation succeeds. The allocation-size overflow case fails closed before calling the shim, as
the native `new (nothrow) uint32_t[]` cannot produce a valid map in that case. The native
allocation-failure `log_init_error_to_host` string is still deferred until string-free GC init
logging is ported.
The region-map endpoint writers `make_busy_block` and `make_free_block`, plus the terminal-space
allocator `allocate_end`, now preserve the native endpoint-only block encoding, high-bit free
marker, forward/backward pointer movement, exact-fit boundary behavior, and counter ownership
(the caller still adjusts `total_free_units`). Their native `dprintf(REGIONS_LOG)` debug lines
and `ASSERT_HOLDING_SPIN_LOCK(&region_allocator_lock)` checks are deliberately deferred until
string-free region logging and broader spin-lock ownership diagnostics are ported; this slice
does not substitute managed diagnostics for them.
`region_allocator::delete_region` and `delete_region_impl` are translated too: the public wrapper
uses the same spin-lock enter/leave shape, and the implementation preserves the native aligned
start assumption, endpoint decoding, left/right free-unit counter routing, previous/next
free-block coalescing, terminal-end contraction, region-map pointer updates, and
`total_free_units` update order. Native region logging and map printing remain deferred.
The private `region_allocator::allocate` free-block search is now translated on top of that
state. It keeps the native lock lifetime, forward/backward scan endpoints, used-free-unit fast
gate, backward endpoint read from `current_index - 1`, free-block fit and split placement,
left/right used-free counters, terminal-space fallback, `total_free_units` update order, and
callback failure rollback through `delete_region_impl`. The C++ callback typedef is represented
as a managed static function pointer returning a byte (`delegate*<byte*, byte>`): this follows
the same internal convention as GC vtable slots implemented by this assembly, avoids delegates
and reverse P/Invoke thunks, and maps native `bool` to an explicit one-byte result.
The public `allocate_region`, `allocate_basic_region`, and `allocate_large_region` wrappers are
translated too, including basic/large alignment, default large-region sizing, generation-to-ETW
segment type selection, output writes after failed allocation, and the native
`GCCreateSegment_V1` event call that still fires when no region was allocated.
The adjacent inline public `gcpriv.h` accessors are now present too: `get_va_memory_load`,
`get_free`, `get_used_region_count`, `get_start`, and `get_left_used_unsafe`, preserving the
native pointer-difference arithmetic, target-width free-byte product, right-map-unused debug
assertion, and raw pointer returns.
That unlocks the
remaining `region_free_list.cpp` helpers: `get_region_kind`, the kind-dispatch wrappers
(`add_region`, `add_region_descending`, `is_on_free_list`), and `unlink_smallest_region` with
its native large-region minimum assertion and early-break control flow.
`region_allocator::move_highest_free_regions` is translated as the caller-locked high-to-low
region-map scan over busy basic-or-large blocks, preserving the destination-list exclusion,
source unlink, destination add, and signed quota break. The formerly deferred initial-region
reservation state after `init` is now represented by `allocate_initial_regions` from `init.cpp`:
it owns the unmanaged
`initial_regions[heap][generation][start/end]` table and reserves, in native order, one forward
large POH region per heap, forward basic SOH regions from gen2 through gen0, then one forward
large LOH region per heap. The table entries are the exact start/end outputs of the allocator;
the allocator's map and left/right boundaries remain its owner, and these null callbacks do not
extend bookkeeping coverage. Initial segment construction and table destruction remain with their
later lifecycle owners.
The first `memory.cpp` slices are now translated in `GCMemory.cs`:
`virtual_alloc_commit_for_heap`, `virtual_commit`, `reduce_committed_bytes`,
`virtual_decommit`, and `virtual_free`, plus the WKS `USE_REGIONS` `decommit_region` and
`decommit_step` paths. The port includes the minimal native accounting and policy state they
touch (`recorded_committed_*` buckets, `committed_by_oh`, `current_total_committed`,
`current_total_committed_bookkeeping`, `heap_hard_limit`, `heap_hard_limit_oh`,
`check_commit_cs`, `reserved_memory`, `never_decommit_p`, `settings.pause_mode`,
`global_regions_to_decommit`, and the background mark-array pointers used by region cleanup).
It preserves hard-limit preflight, rollback on OS commit failure, bookkeeping-vs-heap
accounting, never-decommit heap-memory bypasses and direct decommit accounting, release-only
reserved-memory reduction, GCFreeSegment event firing, page-aligned region decommit ranges,
failed-decommit and never-decommit clearing extents, region used/committed updates, mark-array
decommit flag cleanup and accounting, allocator deletion, and the time-quota/free-list early
return of `decommit_step`. `ManagedGCHeap.Initialize` explicitly initializes the
commit-accounting critical section before heap memory can use these helpers. Ephemeral segment
decommit workflows later in `memory.cpp` remain deferred until ephemeral generations and
segment-decommit state are present.
`thread_free_obj` remains deferred with the free-list object representation. The schema forks on
`USE_REGIONS`,
gcpriv.h's region layout that replaces
`allocation_start` and `plan_allocation_start`(`_size`) with `tail_region`/`tail_ro_region`.
gcpriv.h defines `USE_REGIONS` as `HOST_64BIT && !BUILD_AS_STANDALONE && !__sun && (!HOST_APPLE ||
HOST_OSX)`; this integrated port is never `BUILD_AS_STANDALONE` and never targets illumos, so it
reduces to 64-bit AND not an Apple mobile platform (iOS/tvOS/MacCatalyst) -- OSX and every
non-Apple 64-bit target use regions. The build defines a matching `USE_REGIONS` symbol for the
managed sources, and `GCInterfaceOffsets.cspp` recomputes it from the same primitives so the pinned
table selects the same layout; the native verifier gets the real definition through `gcinternal.h`.
`BACKGROUND_GC` is likewise defined for every non-WASM managed full-runtime build. The managed
runtime uses the workstation segment layout; `GCInterfaceOffsets.h` retains its
`MULTIPLE_HEAPS` server branches so both native verifier targets continue to validate their
distinct layouts.
The adjacent `seg_mapping`/`ro_in_entry` schema is translated with the segment: region builds
embed a complete `heap_segment`, while non-region builds preserve the boundary, server-only
heap pointers, and `seg0`/low-bit-tagged-`seg1` fields. Region builds now also carry the global
skewed `seg_mapping_table` pointer, the explicit initialization-only
`gc_heap.min_segment_size_shr` state, and the `gcinternal.h` direct mapping helpers
(`seg_mapping_word_of`, `get_region_info`, `get_region_info_for_address`,
`get_basic_region_index_for_address`, and `is_free_region`). Large-region continuation entries
preserve the native negative `allocated` sentinel backtracking. Startup rejects configured region
sizes at or above `MAX_REGION_SIZE` and non-power-of-two sizes before deriving the shift; adaptive
default sizing and range-per-heap validation remain deferred. Non-region address-to-segment or
heap lookup algorithms remain deferred to `regions_segments.cpp` and `gc.cpp`, when the required
heap constants and state are available.
The `regions_segments.cpp` port now covers the opening WKS `USE_REGIONS` lifecycle slice:
segment alignment, segment-mapping and region-to-generation table sizing, read-only segment
index clipping, the read-only segment-table marker, brick/card cleanup, background changed-
segment recording and debug mark-array verification, and returning a live region to the
per-heap free lists. It preserves the native absolute-index arithmetic, `ro_in_entry` sentinel,
UOH brick-skip, committed-byte transfer from the owning object heap to the free bucket,
descending free-list dispatch, and basic-region `allocated` sentinel clearing. The native remove
helper remains its intentional no-op. The next dependency-closed lookup slice adds `region_of`,
`get_region_at_index`, and direct `get_region_gen_num(heap_segment*)` access. `region_of` indexes
the already-skewed mapping table with the absolute address shift, while `get_region_at_index`
first adds the shifted nonzero heap base. The region-build `get_uoh_start_object`,
`get_soh_start_object`, and `get_soh_start_obj_len` helpers are also translated: both starts are
the region's memory and the SOH length is zero. The adjacent byte-sized `region_info` map,
including its current/planned generation and demotion/sweep flags, is now represented with both
the absolute and skewed table pointers. Map reads and safe flag updates preserve the native
absolute-versus-skewed indexing; Debug checks cross-check map reads against embedded segment
fields, while the flag updates change both representations.
The synchronization-sensitive region write-barrier slice is now translated. `GCWriteBarrier`
preserves the `GCWriteBarrier` flavor selection of `gc.cpp`, including the zero-initialized
`WriteBarrierParameters` requirement of the server flavor, and publishes ephemeral ranges with
`StompEphemeral`, the skewed map, and the basic-region shift. Its global spin lock is explicitly
initialized during `ManagedGC_Initialize`, without a static constructor; the collector's initial
empty range is the native `MAX_PTR`/null pair, represented by `(byte*)nuint.MaxValue` and null.
`set_region_gen_num` updates the embedded segment generation and every basic-region map entry
before acquiring that lock for gen0/gen1. A contending updater rechecks whether another updater
already covered the range, and an expanding updater stomps the write barrier before publishing
the new bounds, then releases with a volatile store. `make_heap_segment` and
`allocate_new_region` now allocate basic, large, and huge regions through the translated
allocator, commit and account for the initial page, publish the segment fields through the
region mapping table, and return an allocation to the allocator if commit fails. The callback
now grows the native-shaped bookkeeping coverage before an allocator can return a new high-water
region: it reserves the card/brick/generation-map/segment-map/mark-array layout, commits each
required card-through-segment-map range with page boundaries, rolls back partial commits, tracks
the committed coverage and per-element sizes, and retries a failed speculative doubling at the
minimum range. The table pointers are initialized before the translated card table is published;
write-barrier stomping remains with the later collector initialization that owns it. `init_heap_segment`
mechanically resets segment allocation state, preserves only an existing region's
mark-array-committed flag, clamps the region generation, and initializes large-region
continuation sentinels. `init_table_for_region`
now commits and verifies the background mark array for the saved range, propagates commitment
failure by decommitting the region, preserves already committed mark arrays, and initializes only
the first SOH brick. Its dependency-closed mark-array range/new-segment commitment and debug
verification helpers retain the native page and mark-word boundaries, secondary card-table
handling, and region partial-commit assertion.
`get_free_region` now selects basic, large, and smallest-fitting huge regions from the local
free lists, and falls back to the explicit WKS global huge list only while the caller holds the
translated GC spin lock. Reused regions are reinitialized with `existing_region_p`, transfer
committed bytes from the free bucket to the selected object-heap bucket under `check_commit_cs`,
then initialize their tables; a miss follows `allocate_new_region`, and a table-init failure
returns null after its native decommit path. The global huge-list lock is a real explicit
compare-exchange/volatile-store leaf initialized during `ManagedGC_Initialize`, not a managed
lock or a synthetic success path. This WKS project does not define `MULTIPLE_HEAPS`; its
per-heap accounting/debug branch and cross-heap free-region work remain deferred with the server
collector. As in C++, this path does not set LOH/POH flags: deferred generation-threading callers
own those flags.
The next construction/threading slice adds the raw contiguous generation-table accessor,
`make_generation` from `init.cpp`, read-only-skipping segment traversal, `thread_uoh_segment`,
and `get_new_region` from `plan_phase.cpp`. It resets allocation/free-list state while retaining
the generation's initialized allocator shape, wires start/allocation/tail segments, preserves
append order, and assigns LOH/POH flags at the native `get_new_region` owner before publishing
the new tail. The WKS initial SOH/UOH constructors now consume `initial_regions` through that
same raw generation-table adapter: SOH constructs gen2 through gen0, stops immediately on a
failed segment commit, and publishes the gen0 ephemeral segment before its allocation pointer;
UOH sets its native LOH/POH flag before generation construction. `ManagedGCRegionBootstrap`
now runs that reservation, bookkeeping, initial-region and initial-generation sequence during
production `IGCHeap::Initialize`. Its unmanaged WKS `gc_heap` owns the generation table,
dynamic allocation state, ephemeral segment, allocation counters, and SOH/UOH more-space locks;
it also owns the range and initial-region state and unwinds every allocation and reservation on
failure. Production SOH/UOH allocation-context refills use this heap through
`create_try_allocate_more_space_context` and `allocate_more_space`. Their plain managed callback
owns the WKS lock and the explicit non-collecting bootstrap budget policy consumes initial
regions without claiming an unported collection ran.
The two trailing gen2 fields follow `DOUBLY_LINKED_FL` (`TARGET_64BIT && !TARGET_WASM`), and the
diagnostic-only `FREE_USAGE_STATS` fields, never defined, are omitted. `USE_REGIONS` implies
`HOST_64BIT`, so the 32-bit column of the region branch in the table is never evaluated. The class
has no native constructor, so zero initialization matches its default for every field except the
embedded `free_list_allocator`, which the native allocator constructor brings up when the
containing `gc_heap` is created; `generation.initialize` reproduces that explicitly because C# does
not run struct constructors for embedded or unmanaged storage. The `generation_*` accessors are
static helpers taking a `generation*`, preserving the native reference-return API without a managed
reference to collector state.

`GCDesc.cs` translates the compact pointer-map records of `gcdesc.h`: the target-sized
`val_serie_item`, the overlaid `CGCDescSeries` union, the backward-growing `CGCDesc`
descriptor's size, initialization, series-address arithmetic, and MethodTable lookup. The
allocation-free short-object scan in `MarkPhase.cs` consumes normal and negative-count repeating
maps with the native `go_through_object_nostart` order. Native static assertions and direct tests
cover normal, component-array, no-pointer, and negative-count repeating descriptors.

`gceventstatus.h`, `gcevent_serializers.h`, and the current `gcevents.h` table are translated.
`GCEvents.cs` writes out the x-macro expansion in the native table's order: every known event
checks its provider state and calls the corresponding `IGCToCLREventSink` slot, while each of
the four current dynamic events emits its null-terminated native name, the serialized
`uint16_t` version, and the arguments from its native call site in order. The serializer
preserves the native primitive sizes, cursor advancement, little-endian integer payloads, and
raw `float` representation. NativeAOT's supported targets are little-endian; when a big-endian
target exists, the three integral serializers need the native header's `BIGENDIAN` byte-swap
branch. The only omitted body is `DebugDumpState`, an `fprintf` dump behind the disabled
`TRACE_GC_EVENT_STATE` define; it waits for the GC's string-free tracing support.

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

`SoftwareWriteWatch.cs` is that software write watch: the port of `softwarewritewatch.h` and
`softwarewritewatch.cpp`, which is a byte-per-page dirty table over the heap rather than an
operating system feature, so it works everywhere `GCToOSInterface`'s write watch does not. The
table pointer is translated -- biased by the table byte index of the heap's low bound -- so that
every lookup indexes it directly by an absolute address instead of subtracting the heap start
first, exactly as `TranslateTableToExcludeHeapStartAddress` does; `SetResizedUntranslatedTable`
copies the old table's bytes into the new one at the same bias before the caller moves the
published heap bounds. `EnableForGCHeap` and `DisableForGCHeap` are `WriteBarrierOp.
SwitchToWriteWatch`/`SwitchToNonWriteWatch` calls through `GCToEEInterface.StompWriteBarrier`,
the same vtable call `GCHeapMemory.Initialize` uses to publish the card tables.
`GetDirty`/`GetDirtyFromBlock` scan the table a machine word at a time and bit-scan each nonzero
word to find which of its bytes are set, issuing `GCEnv.MemoryBarrierProcessWide` -- a
`[RuntimeImport]` over `minipal_memory_barrier_process_wide`, next to the rest of the
environment's process-wide primitives -- before reading dirty state on an unsuspended runtime and
again after clearing it, exactly where the C++ comments say a cross-thread barrier is needed.
`GetTableStartByteOffset` is declared by the header but has no definition or caller anywhere in
`src/coreclr`, so it has no C# counterpart; inventing a body would not be a translation of
anything. The heap bounds `GetHeapStartAddress`/`GetHeapEndAddress` read are
`GCCommon.g_gc_lowest_address`/`g_gc_highest_address`, the same globals `gccommon.cpp` declares;
`GCHeapMemory.Initialize` is what publishes them today, and `HeapStart`/`HeapEnd` simply read them
back, so there is one authoritative place the heap's bounds are set.

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
`ManagedGC_Windows_GetSystemInfoProcessorCount`, `ManagedGC_Windows_GetProcessAffinitySet`,
`ManagedGC_Windows_GetCanEnableGCCPUGroups`, `ManagedGC_Windows_GetCpuGroupCount`,
`ManagedGC_Windows_GetCpuGroupActiveProcessorCount` and `ManagedGC_Windows_GetCpuGroupBegin` on
Windows. The Windows total is a `uint32_t*` rather than a value because the C++ body caches into
`g_totalCpuCount` on first call and the port must perform that same write; the others are values
because the C++ only reads them. `GetTotalProcessorCount` is also the first C++ body the port
replaces that must stay compiled: `PalUnix.cpp` and `PalMinWin.cpp` call it, and both are in the
managed runtime archive. `CanEnableGCCPUGroups` is translated and still compiled on Windows for
the same reason -- `PalMinWin.cpp` calls it, and so does the retained `GetTotalProcessorCount` --
so those are the only translated methods of this layer that `FEATURE_MANAGED_GC` leaves in place.
`GetMaxProcessorCount`, the Unix `CanEnableGCCPUGroups` and `ParseGCHeapAffinitizeRangesEntry`
were retained for `gcconfig.cpp`'s `ParseGCHeapAffinitizeRanges`, and are excluded now that it is
translated too.

The affinity, NUMA and CPU-group methods follow, and they are translated the same way. On Unix
`SetCurrentThreadIdealAffinity` and `GetCurrentThreadIdealProc` keep the C++ no-op and `false`,
`SetThreadAffinity` builds the same cpu set the C++ builds with
`CPU_ALLOC_SIZE` / `CPU_ZERO_S` / `CPU_SET_S` -- one pointer-sized word per `8 * sizeof(nuint)`
processors, rounded up, taken from `ManagedGC_AllocZeroed`, with the same bounds check
`CPU_SET_S` performs -- and passes it to `sched_setaffinity(0, ...)`, or to
`pthread_setaffinity_np(pthread_self(), ...)` on the one platform where only that configure check
holds, which is the same two-level `#if` the C++ has. `BoostThreadPriority` is the
`[LOCALGC TODO]` `false`, `SetGCThreadsAffinitySet` filters the process affinity set in place,
and `GetProcessorForHeap` and the `mbind` half of `VirtualCommitInner` reach the NUMA state
through `ManagedGC_Unix_GetNumaAvailable`, `ManagedGC_Unix_GetHighestNumaNode`,
`ManagedGC_Unix_GetNumaNodeNumByCpu` and `ManagedGC_Unix_BindMemoryPolicy`. The last two exist
because `numasupport.h` declares its functions with C++ linkage, so the managed side cannot name
their mangled symbols. `HAVE_SCHED_SETAFFINITY` is spelled as the same platform list as
`HAVE_SCHED_GETCPU`, `HAVE_PTHREAD_SETAFFINITY_NP` as that list plus FreeBSD, and both are
`static_assert`ed on every arm; the `TARGET_LINUX && !TARGET_ANDROID` of the two
NUMA blocks is the `HAVE_SCHED_GETCPU` list minus Android, and `gcenv.managed.cpp` `#error`s if
the two selections ever name different platforms. The node mask arithmetic of the C++ -- which counts `sizeof`
rather than bits -- is translated as written rather than corrected, as everything else here is.

On Windows the same methods are the Win32 calls the C++ makes: `SetThreadIdealProcessorEx` and
`GetThreadIdealProcessorEx` over `PROCESSOR_NUMBER`, `SetThreadGroupAffinity` over
`GROUP_AFFINITY` or `SetThreadAffinityMask` when CPU groups are off, `SetThreadPriority` with
`THREAD_PRIORITY_HIGHEST`, and `GetNumaNodeProcessorMaskEx` / `GetNumaProcessorNodeEx` for the
node information, each of them declared as `<windows.h>` declares it and checked against it by
`gcenv.managed.cpp`, together with the `GROUP_AFFINITY` layout the managed code writes into.
`ParseGCHeapAffinitizeRangesEntry` translates the C++ `strtoul` into an allocation-free
`StrToUInt` with the same saturation and end-pointer behavior, and validates the group against
the same table.

Four things that read or write a file cannot be translated without allocating, so they stay
native for now, as the narrowest possible leaves: `ManagedGC_CGroup_GetPhysicalMemoryLimit` and
`ManagedGC_Unix_GetPhysicalMemoryUsed` wrap the `CGroup` class of `gc/unix/cgroup.cpp`, which is
in an anonymous namespace, and `ManagedGC_Unix_ReadMemoryValueFromFile`,
`ManagedGC_Unix_ReadMemAvailable` and `ManagedGC_Unix_GetCurrentVirtualMemorySize` wrap the
`static` `/sys` and `/proc` readers of `gcenv.unix.cpp`. They are deleted with the cgroup parsing
submodule; Windows retains none of them.

The managed runtime archive no longer compiles the Unix `events.cpp`; on Windows,
`FEATURE_MANAGED_GC` excludes the `GCEvent::Impl` section of `gcenv.windows.cpp`, and on both
platforms it excludes the `Sleep` and `YieldThread` section of `gcenv.unix.cpp` and
`gcenv.windows.cpp`, the memory limit and cache sizing sections of `gcenv.unix.cpp`,
`gcenv.windows.cpp` and `gc/unix/cgroup.cpp`, the timer section of `gcenv.unix.cpp` and
`gcenv.windows.cpp`, the four processor identity methods of `gcenv.unix.cpp` and
`gcenv.windows.cpp`, and the affinity, ideal-processor, NUMA, heap-to-processor and
affinitize-range methods of both -- including the anonymous-namespace `GetGroupForProcessor` of
`gcenv.windows.cpp`, whose only caller went with them. Those three files remain in the archive
only for the `GCToOSInterface` services that have not been translated yet, for the two methods
the rest of the archive still calls, and for the leaf helpers and state accessors above; the
workstation and server GC archives still compile every one of those bodies
unchanged.

Two C++ shapes are preserved rather than corrected, because this is a translation: `CloseEvent`
releases the operating system object but neither frees the Impl nor clears the pimpl pointer, so
`IsValid()` keeps reporting true afterwards -- a `GCEvent` deliberately has no destructor, see
[dotnet/runtime#7919](https://github.com/dotnet/runtime/issues/7919) -- and on Windows a failed
`CreateEvent` returns `NULL`, which the C++ `IsValid()` does not recognize as a failure because
it compares against `INVALID_HANDLE_VALUE`.

The retained-native surface is now narrow and state-oriented:

* the three `ManagedGC_OS_*` forwarders that still exist by design: `Initialize`, `Shutdown` and
  `DebugBreak`;
* the Unix cgroup and `/proc` parsing leaves (`ManagedGC_CGroup_*`,
  `ManagedGC_Unix_ReadMemoryValueFromFile`, `ManagedGC_Unix_ReadMemAvailable`,
  `ManagedGC_Unix_GetCurrentVirtualMemorySize`), which remain because they still own
  allocation-heavy parsing code;
* the initialization-owned Unix state accessors used by the translated affinity/NUMA/count paths:
  `ManagedGC_Unix_GetCurrentThreadId`, `ManagedGC_Unix_GetTotalCpuCount`,
  `ManagedGC_Unix_GetConfiguredCpuCount`, `ManagedGC_Unix_GetProcessAffinitySet`,
  `ManagedGC_Unix_GetNumaAvailable`, `ManagedGC_Unix_GetHighestNumaNode`,
  `ManagedGC_Unix_GetNumaNodeNumByCpu` and `ManagedGC_Unix_BindMemoryPolicy`;
* the initialization-owned Windows state accessors used by the translated affinity/NUMA/CPU-group
  paths: `ManagedGC_Windows_GetTotalCpuCount`, `ManagedGC_Windows_GetSystemInfoProcessorCount`,
  `ManagedGC_Windows_GetProcessAffinitySet`, `ManagedGC_Windows_GetCanEnableGCNumaAware`,
  `ManagedGC_Windows_GetNumaNodeCount`, `ManagedGC_Windows_GetCanEnableGCCPUGroups`,
  `ManagedGC_Windows_GetCpuGroupCount`, `ManagedGC_Windows_GetCpuGroupActiveProcessorCount` and
  `ManagedGC_Windows_GetCpuGroupBegin`;
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

## Configuration

`GCConfig.cs` is the port of `gcconfig.h` and `gcconfig.cpp`. The C++ generates the accessors and
the backing fields by expanding the `GC_CONFIGURATION_KEYS` table through three macros; C# has no
such expansion, so the port writes the expansion out, one config at a time, in the table's order,
with the same private and public keys, the same defaults and the same integer widths. All eighty
entries are there: the seventy-five cached boolean and integer configs, each with its
`Get{name}()`, `Get{name}(defaultValue)`, `Set{name}(value)` and its `s_{name}` / `s_{name}Provided`
/ `s_Updated{name}` triple, and the five string configs, which the C++ deliberately does not cache
and neither does this.

A C++ `bool` becomes a `byte`, because the EE writes through the pointer the GC hands it and a
managed `bool` has no guaranteed width; `int64_t` becomes `long`. Each key is a UTF-8 literal with
an explicit terminator, so taking its address produces the null-terminated `const char*` the EE
expects without allocating anything. `Initialize`, `RefreshHeapHardLimitSettings` and
`EnumerateConfigurationValues` are the same three walks of the same table in the same order, and
`ParseGCHeapAffinitizeRanges` -- a free function in the C++, which C# has no place for outside a
type -- keeps every branch, including the ones that are surprising: an empty range list is
accepted, because the C++ breaks out of the loop before it moves the pointer it then tests for
the terminator, and a range list handed in together with a non-zero affinity mask is ignored
rather than rejected.

`GCConfigStringHolder` is translated as a `ref struct`. The C++ class frees the string in its
destructor, deletes its copy operators and is returned by value from the string getters; a ref
struct gives the same guarantees in C# -- `using` releases the string where the C++ scope would,
and the holder cannot outlive the frame that produced it -- without allocating. `Dispose` skips a
null string and clears the pointer afterwards, exactly as the destructor does, so a config the EE
does not have is never handed to `FreeStringConfigValue` and a released holder cannot double-free.

`IGCHeap::EnumerateConfigurationValues` is wired to it: `ManagedGCHeap` forwards the slot to
`GCConfig.EnumerateConfigurationValues` as `GCHeap::EnumerateConfigurationValues` of
`interface.cpp` does, which is what `RhEnumerateConfigurationValues` and therefore
`GC.GetConfigurationVariables()` calls. The smoke test reads the dictionary back.

This is the first `IGCHeap` body of the port that calls one of its own callback parameters, and
it does so through a `delegate* unmanaged[SuppressGCTransition]` view of the pointer, for the same
reason the `IGCToCLR` slots have that type: the C++ calls the EE's callback as plain native code,
without changing GC mode. The parameter itself keeps the type the `ConfigurationValueFunc` typedef
of `gcinterface.h` gives it, which is what the vtable and its verifier name. Doing the call with a
transition would not merely be slower, it would be wrong: the EE reaches the managed `IGCHeap`
methods without a reverse P/Invoke frame, so the transition frame in effect while one of them runs
belongs to the EE -- `RhEnumerateConfigurationValues` is an ordinary P/Invoke away from
`GC.GetConfigurationVariables` -- and a P/Invoke inside the slot would clear it on return, leaving
the thread reporting cooperative mode with a native frame in the middle of a stack that no code
manager can walk. Every future body that invokes a callback parameter directly has to do the
same. A callback that the GC passes back to the EE follows the complementary rule:
`GCScan.GcScanRoots` keeps its promote function as a managed function pointer, and
`GCToEEInterface.GcScanRoots` representation-casts it to the native `promote_func*` only at the
vtable boundary. That keeps ILC from adding the reverse-P/Invoke prologue that
`[UnmanagedCallersOnly]` would require, because the cooperative-mode EE cannot enter such a
callback safely.

The two other consumers the C++ has are not reachable yet, and are left alone rather than half
wired: `GCHeap::RefreshMemoryLimit` calls `RefreshHeapHardLimitSettings` from inside
`gc_heap::refresh_memory_limit`, which recomputes collector state that does not exist, and
`GetLOHThreshold` reports `gc_heap::loh_size_threshold`, which `init_semi_shared` derives from
`GCConfig::GetLOHThreshold()`. Both arrive with the collector.

`FEATURE_MANAGED_GC` now excludes `EnumerateConfigurationValues`, `RefreshHeapHardLimitSettings`,
`ParseIndexOrRange` and `ParseGCHeapAffinitizeRanges` from the C++ `gcconfig.cpp`: nothing in the
managed runtime archive calls them any more. The first two are reached through the `IGCHeap`
vtable, which is the managed heap's; `ParseGCHeapAffinitizeRanges` is called only from
`gc/interface.cpp`, which a managed-GC application does not compile; and `ParseIndexOrRange` had
one other caller, `GCToOSInterface::ParseGCHeapAffinitizeRangesEntry`, which is excluded with it
-- along with `GetMaxProcessorCount` and the Unix `CanEnableGCCPUGroups`, whose only remaining
caller was `ParseGCHeapAffinitizeRanges`. What stays is the storage, the accessors and
`GCConfig::Initialize`, because `PalInit` calls `Initialize` and the still-native
`GCToOSInterface::Initialize` reads `GCNumaAware` and `GCCpuGroup` back out of it on Windows. That
is the same initialization-owned state the processor and NUMA ports reach through shims, and it
goes the same way: with plan step 3's initialization submodule in [ROADMAP.md](ROADMAP.md). The
managed GC therefore keeps its own configuration state, initialized from `ManagedGC_Initialize`,
independent of the native one the PAL fills for the C++ `GCToOSInterface`.

`gcconfig.h`'s `HeapVerifyFlags` and `WriteBarrierFlavor` are translated as ordinary C# enums.
They are not in `GCInterfaceOffsets.h` on purpose: nothing passes them across the GC/EE boundary,
so they are not part of the ABI that table pins, and `GCInterfaceLayoutTests` records that
exemption by name. The configuration table is not an ABI either -- it is a set of strings the GC
asks the EE for -- so it is pinned the other way, by checking the translation against the C++
source itself; see [testing](#testing-the-ported-leaves).

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

The same virtual-memory suite also creates a real region bookkeeping reservation and exercises
the `on_used_changed` callback through its native card-table layout: high-water growth, an
in-coverage no-op, hard-limit commit failure without coverage mutation, page-rounded commitment,
and a later region allocation that writes the committed brick, mapping, and generation entries.

`SoftwareWriteWatchTests` runs the software write watch port itself, over a synthetic heap of
unmanaged memory and a table sized by the port's own `GetTableByteSize`: `SoftwareWriteWatch`
never dereferences a heap address, only shifts it into a table index, so a heap-shaped range of
addresses is all a test needs. It substitutes `GCToEEInterface.StompWriteBarrier` -- there is no
NativeAOT write barrier to bash in a test process -- and `GCEnv.MemoryBarrierProcessWide`, which
in the shipping build is a real cross-thread barrier and here only counts its calls, over
`tests/GCEnv.MemoryBarrierProcessWide.TestHost.cs`. The tests cover table sizing and alignment,
the translated table pointer against the raw bytes of the buffer it is translated from,
`SetResizedUntranslatedTable` preserving dirty bits at their same absolute addresses across a
resize, `StaticClose`, the exact `WriteBarrierOp`, table pointer and suspended flag
`Enable`/`DisableForGCHeap` stomp, page-boundary-exact `ClearDirty`/`SetDirty`/`SetDirtyRegion`,
and `GetDirty` across a single table block, across several, over an arbitrary subrange, at the
edge of the caller's output capacity, with dirty state retained versus cleared, with every
bit-scan position of a table word mapped to its own page, and with the process-wide barrier
called only when the runtime is not already suspended and only as many times as the C++ comments
say it must be.

`GCWriteBarrierTests` uses the same substituted `GCToEEInterface.StompWriteBarrier` call to
verify every `GCWriteBarrier` flavor, exact ephemeral stomp arguments, the multi-basic-region
map update, gen2's lack of ephemeral publication, monotonic range expansion, lock release, and
the ordering that exposes new barrier state before global bounds. A controlled contending thread
also verifies that a concurrently covered range suppresses a redundant stomp.

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

`GCProcessorTests` covers processor counts, identity, affinity, NUMA and CPU groups. It checks
the one-call forwarding/widening behavior of identity methods, both `HAVE_SCHED_GETCPU` arms,
and the Windows `GroupProcNo` packing corners; it checks `GetTotalProcessorCount` caching and
source selection and `GetMaxProcessorCount` capacity semantics; and it drives the translated
affinity/NUMA/CPU-group bodies through recording substitutes: thread affinity masks and group
affinity, ideal-processor set/get paths, thread-priority boosting, process-affinity-set
filtering by set and by mask, NUMA/node and CPU-group info aggregation, heap-to-processor mapping
including node fallback rules, and parsing of affinitize-range entries.

`GCConfigTests` covers the configuration port, and it is the one place where what is substituted
is the EE rather than libc: `tests/GCToEEInterface.TestHost.cs` stands in for the four config
methods of `GCToEEInterface`, whose shipping versions are indirect calls through the `IGCToCLR`
vtable the EE hands the GC and which therefore cannot run in a test process. It models what
`nativeaot/Runtime/gcenv.ee.cpp` does -- the private key first, the public key only when the
config has one, a boolean narrowed to the EE's `bool`, an integer that is the environment's
`uint64` reinterpreted, and a string the EE owns until it is given back -- and records every
request, so the tests assert the key sequence the port asks for and not only the values it caches.

Half of the class is table-driven, from `gcconfig.h` and `gcconfig.cpp`, which are embedded in the
test assembly as `GCInterfaceOffsets.h` is: each of the eighty configs has a test that it is
translated with the right accessor and field types, and a test that its default matches the one
the C++ declares -- including `LOHThreshold`, whose default is `LARGE_OBJECT_SIZE` and is
therefore read out of the `GCInterfaceOffsets.h` entry the native build asserts against `gc.h`.
Two more check that the port has not grown a config the C++ does not have and that the field
order is the table's, and the `Initialize`, `RefreshHeapHardLimitSettings` and
`EnumerateConfigurationValues` tests compare the whole recorded key sequence against the same
table, so a config that is missing, out of order, or asked for with the wrong key fails on that
entry.

The other half is behavior: that an unprovided config takes the caller's default and a provided
one does not; that `Set` moves only the value `EnumerateConfigurationValues` reports; that a
config whose public key is `NULL` is asked for with a null pointer and never reaches the public
settings; that every bit of an `int64` survives, including the patterns that read back negative,
while a boolean arrives narrowed; that a value the EE writes while reporting "not provided" is
still cached, which is what the C++ gets from handing over the address of the cached value
itself; that `Refresh` re-reads the eight hard-limit configs without making them provided; that
the enumeration reports the string configs by pointer, still owned by the EE while the callback
runs, and frees each of them exactly once afterwards while never freeing one the EE did not
provide; and that the holder frees once, tolerates a second release and never frees null. The
enumeration callback is the address of an ordinary managed static rather than an
`[UnmanagedCallersOnly]` method, because the port calls it without a GC transition and a reverse
P/Invoke prologue would reject a caller that is already cooperative; the runtime compiles a static
with a blittable signature to the platform C ABI, which is the same property that lets the native
EE call this port's `IGCHeap` slots, so those ten tests are conditioned on the one architecture
where the two conventions differ. The
affinitize-range half drives `ParseGCHeapAffinitizeRanges` over the same substituted imports the
processor tests use: the mask-only and range-only cases, the CPU-group rejection, the ranges that
are ignored because a mask was given, the mask folding of an index past the first bitset word, the
empty string the C++ accepts, and the malformed and out-of-range lists it rejects -- with the
highest legal index checked so that a Debug run proves the `AffinitySet::Add` assert is not
reachable from a well-formed list.

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

On region targets, startup also reserves the configured `GCRegionRange` (or the current 256 MB
bootstrap default), builds WKS bookkeeping, reserves initial regions, and constructs initial
SOH/LOH/POH generation state. `ManagedGCHeap.Alloc` uses that region heap for SOH and UOH
allocation-context refills. `GCHeapMemory` remains a separate non-collecting bootstrap range for
unmanaged frozen-segment metadata; it neither changes the region range nor publishes card tables
or write-barrier bounds on region targets. Region collection and UOH segment retry policy remain
deferred, so exhausted UOH routing returns null rather than reporting a collection.

`GCAllocation` now writes the native-shaped free-object method table, array length, and
doubly-linked free-list marker for object gaps, carries the allocation-limit arithmetic leaves,
and transitions a selected allocation context through a WKS region refill. The latter fills a
discontinuous hole, pads a contiguous gen0 context, updates its limit/accounting, advances the
selected SOH/UOH allocation pointer, computes and clears the native right-edge span, applies
zeroing-optional syncblock/object rules, and publishes the segment's used boundary. It releases
the selected more-space lock through the unmanaged callback before clearing, and the wrapper
does not release it again. BGC mark-bit tracking, allocation events, brick updates, and
verification remain explicit collector-owned deferrals.
The segment-end leaf also selects the committed or reserved endpoint, derives the allocation
limit, grows the segment through the accounted virtual-commit helper, propagates commit and
hard-limit failures, and hands the range to that refill transition. Its UOH wrapper walks
writable segments and records end-segment allocation. The deferred heap-owned dynamic-data
table, allocation quantum, generation table, allocation pointer, ephemeral segment, selected
SOH/UOH total, and heap number are explicit unsafe parameters, rather than a partial heap
layout. The dependency-closed SOH and initial-UOH refill paths are routed through these helpers.
UOH segment retry and collection dependencies remain deferred, so allocation stops honestly when
the initial region is exhausted.

The next allocation orchestration leaf connects those fits in native order:
`soh_try_fit` favors the SOH free list, honors the short-end result, tries the current
ephemeral region, fixes its allocation context before advancing to an existing or newly acquired
region, and preserves commit failure; `uoh_try_fit` takes the corresponding UOH free-list or
writable-segment path and preserves its `oom_reason`. `short_on_end_of_seg` intentionally
receives the two results of the unported planning policy as explicit booleans, rather than
reimplementing dynamic space/budget decisions. Likewise,
`fix_allocation_context_for_region_rollover` is only the native
`for_gc_p == true, record_ac_p == false` rollover call: it formats or rewinds the context,
retires SOH accounting, and `fix_youngest_allocation_area` publishes the old region's allocation
pointer. Concurrent verification, allocation-context statistics, diagnostic region-added events,
and all production allocation routing remain deferred.

The next `try_allocate_more_space` slice is an explicit unmanaged state-machine core. It preserves
the WKS `allocation_state` transitions around the translated SOH/UOH fit paths, including the
initial/after-BGC/after-compacting-GC branches, segment-acquisition retry states, commit failure,
short-end, `oom_reason`, allocation flags, selected generation, free-list/segment budget
mutations, retry-other-heap exit, and failure lock-release order. Its context holds only explicit
unmanaged heap inputs. Its WKS heap now initializes the native `static_data_table` values and
each generation's `dynamic_data` pointer, min-size, clocks, current/promoted/collection/
fragmentation counters, and initial SOH/LOH/POH allocation budgets from
`dynamic_tuning.cpp`. The native cache-, configured-segment-, Gen0/Gen1-budget-, latency-level-,
write-watch/concurrent-budget-, and region-independent UOH rules are retained; collection-time
retuning and hard-limit computation remain deferred. GC/BGC waits and triggers, full-GC
notifications, more-space locks, UOH acquisition, retry decisions, and OOM reporting are an
unmanaged function-pointer protocol;
a null callback returns the exact state and deferred operation rather than claiming that such work
succeeded. This core is deliberately not wired to `ManagedGCHeap.Alloc`.
The WKS `allocate_more_space` wrapper now retries from the native initial state, clears transient
retry/OOM/lock state before each re-entry, and returns whether the final state can allocate.
`create_try_allocate_more_space_context` now supplies the translated WKS heap-owned fields, and
its unmanaged callback enters/leaves the selected SOH/UOH lock and performs the WKS
`new_allocation_allowed` check, including the gen0 elapsed-time throttle. GC/BGC waits and triggers, full-GC notification,
segment acquisition, retry policy, and OOM handling still return the exact deferred operation.
When this terminal wrapper returns a deferred failure after acquiring a concrete lock, it releases
that lock while preserving the pending operation for its caller. `ManagedGCHeap.Alloc` uses it
for SOH and initial-UOH refills; the managed allocation callback is a plain managed function
pointer because this protocol never crosses a native boundary.

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
the C++ collector, native handle table, GC loader, common helpers, bridge, scanner, event status,
and software write watch. Applications that do not opt in continue to link
`Runtime.WorkstationGC` or `Runtime.ServerGC`.

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
  `gcheaputilities.cpp`, and the managed runtime source list omits `gc/gcload.cpp` and
  `gc/gccommon.cpp`, preventing static archive extraction from pulling those native
  implementations back in.

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
  during GC startup. Where the table selects a schema on a gcpriv.h feature switch that the switch
  itself defines (such as `USE_REGIONS` for the `generation` layout), the `.cspp` recomputes that
  switch from the same primitives the compile definitions carry, since it does not include
  gcpriv.h; the native verifier gets the real definition through `gcinternal.h` instead.
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
