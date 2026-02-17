# Bazel Build for dotnet/runtime

## Goal

Build all artifacts in dotnet/runtime with Bazel — native C/C++ components,
managed C# libraries, and the assembled runtime layout — eventually replacing
the CMake + MSBuild build pipeline. The Bazel build must produce equivalent
output binaries and support all platforms CMake/MSBuild currently targets.

## Current Status

The Bazel build produces a fully functional .NET runtime on **linux-x64**.
All native C/C++ components build with Bazel (CoreCLR, corehost, 6 native
interop libs, NativeAOT runtime). Managed C# libraries (System.Private.CoreLib,
44 framework assemblies) also build with Bazel via `rules_dotnet`.
A hybrid build script assembles everything into a standard `dotnet` runtime
layout.

### What Works

- **Native C++**: libcoreclr.so (with statically-linked JIT), dotnet host,
  hostfxr, hostpolicy, apphost, nethost, 6 native interop libraries,
  NativeAOT runtime, standalone GC, AOT JIT interface
- **Managed C#**: System.Private.CoreLib, 44 framework assemblies
  (System.Runtime, System.Collections, System.Console, System.Linq, etc.),
  ref assemblies, source generators
- **Build tools**: ResGen, GenerateResxSource, GenFacades, ilasm, LibraryImportGenerator
- **Tests**: corehost native tests, xUnit-based managed test infrastructure,
  2 library test suites (System.Runtime.Numerics, System.Diagnostics.Contracts)
- **Per-component configuration**: independent debug/checked/release for
  CoreCLR and Libraries (matching MSBuild's `-rc`/`-lc` flags)
- **Runtime layout**: `runtime_layout` rule assembles stripped binaries into
  standard .NET hosting directory structure

### What's Next

- Remaining managed libraries (~156 assemblies not yet in Bazel, out of ~200 total)
- Library unit tests (2 of ~187 libraries have Bazel test BUILD files)
- CoreCLR diagnostic tooling: DAC (mscordac), DBI (mscordbi), createdump, SOS
- CoreCLR tools: SuperPMI, ildasm (full binary), crossgen2
- ILC (NativeAOT ahead-of-time compiler) and crossgen2 (ReadyToRun compiler)
- ILLink (IL trimmer/linker)
- Installer/packaging (NuGet packs, runtime packs, targeting packs)
- Additional platforms (linux-arm64, macOS, Windows)
- Full test suite integration
- Eliminate MSBuild dependency for managed builds

## Per-Component Configuration

CoreCLR and Libraries have independent build configurations, matching
MSBuild's `-rc` (runtime configuration) and `-lc` (libraries configuration):

| Config flags | CoreCLR | Libraries |
|---|---|---|
| *(default)* | Debug | Debug |
| `--config=release` | Release | Release |
| `--config=clr_release` | Release | Debug |
| `--config=clr_checked` | Checked | Debug |
| `--config=libs_release` | Debug | Release |
| `--config=clr_checked --config=libs_release` | Checked | Release |

Implementation uses modern Bazel `string_flag` build settings (`//:clr_config`,
`//:libs_config`) with `per_file_copt` scoping C++ defines by source path.
Clang is the default compiler (matching CMake).

## Verifying Build Equivalence

`compare-bazel.sh` automates build-input equivalence checking between Bazel and
CMake/MSBuild. It compares every compilation unit's source files, preprocessor
defines, compiler flags, references, and other inputs.

```bash
# Prerequisites: build both systems first
./build.sh clr+libs -rc release                         # MSBuild + CMake
bazel build //...                                       # Bazel

# Run comparison
./compare-bazel.sh                                      # default (debug)
./compare-bazel.sh --config release                     # release mode
./compare-bazel.sh --skip-build                         # reuse existing build artifacts
./compare-bazel.sh --verbose                            # show full diffs
./compare-bazel.sh --json-output results.json           # machine-readable output
```

The tool lives in `eng/tools/BuildEquivalenceCheck/`. It parses MSBuild `.binlog`
files, CMake `compile_commands.json`, and Bazel `aquery` output to extract and
normalize compilation records, then compares them field-by-field.

### Known equivalence gaps

See [build-equivalence-TODO.md](build-equivalence-TODO.md) for the full list. Key
areas:

- **Native defines** (1000 files): Boolean define normalization needed (`-DFOO` vs
  `-DFOO=1`), plus missing/extra defines in `coreclr_defs.bzl` and `native_defs.bzl`
- **Native flags/optimization** (1000 files): Warning flags and optimization level
  mismatches between `.bazelrc` and `CMakeLists.txt`
- **Managed source files** (23 assemblies): Missing `SkipLocalsInit.cs`, `Forwards.cs`
  generation gaps, facade build strategy differences
- **Managed nowarn** (23 assemblies): MSBuild's `Directory.Build.props` suppressions
  not replicated in Bazel
- **Unmatched compilations**: 640 CMake-only + 405 MSBuild-only units not yet ported
  to Bazel

## Platform Support Status

CMake currently supports all of these OS × architecture combinations. Bazel support status is tracked below.

### Operating Systems

| OS | CMake | Bazel | Notes |
|----|-------|-------|-------|
| Linux (glibc) | ✅ | 🔨 In progress | First target; linux-x64 compiler flags verified |
| Linux (musl/Alpine) | ✅ | ❌ Not started | |
| macOS (Darwin) | ✅ | ❌ Not started | |
| Windows | ✅ | ❌ Not started | |
| FreeBSD | ✅ | ❌ Not started | |
| NetBSD | ✅ | ❌ Not started | |
| OpenBSD | ✅ | ❌ Not started | |
| illumos/Solaris (SunOS) | ✅ | ❌ Not started | |
| Haiku | ✅ | ❌ Not started | |
| Android | ✅ | ❌ Not started | |
| iOS / iOS Simulator | ✅ | ❌ Not started | |
| tvOS / tvOS Simulator | ✅ | ❌ Not started | |
| Mac Catalyst | ✅ | ❌ Not started | |
| Browser (Emscripten/WASM) | ✅ | ⊘ Out of scope | Mono only |
| WASI | ✅ | ⊘ Out of scope | Mono only |
| Tizen | ✅ | ⊘ Out of scope | Mono only |

### Architectures

| Architecture | CMake | Bazel | Notes |
|-------------|-------|-------|-------|
| x64 (AMD64) | ✅ | 🔨 In progress | First target |
| x86 (i386) | ✅ | ❌ Not started | |
| ARM64 (AArch64) | ✅ | ❌ Not started | |
| ARM (32-bit) | ✅ | ❌ Not started | |
| ARMv6 | ✅ | ❌ Not started | |
| RISC-V 64 | ✅ | ❌ Not started | |
| LoongArch64 | ✅ | ❌ Not started | |
| s390x | ✅ | ❌ Not started | |
| PowerPC64 (ppc64le) | ✅ | ❌ Not started | |
| MIPS64 | ✅ | ❌ Not started | |
| WASM | ✅ | ⊘ Out of scope | Mono only |

---

## 1. Native Libraries (`src/native/libs/`)

### 1.1 System.Native — ✅ DONE
- [x] `src/native/libs/System.Native/BUILD.bazel`
- [x] Produces `libSystem.Native.so` (277 exports, matches CMake)

### 1.2 System.IO.Compression.Native — ✅ DONE
- [x] `src/native/libs/System.IO.Compression.Native/BUILD.bazel`
- [x] Produces `libSystem.IO.Compression.Native.so` (472 exports, matches CMake)

### 1.3 System.IO.Ports.Native — ✅ DONE
- [x] `src/native/libs/System.IO.Ports.Native/BUILD.bazel`
- [x] Produces `libSystem.IO.Ports.Native.so` (19 exports, matches CMake)

### 1.4 System.Net.Security.Native — ✅ DONE
- [x] `src/native/libs/System.Net.Security.Native/BUILD.bazel`
- [x] Produces `libSystem.Net.Security.Native.so` (21 exports, matches CMake)
- [x] Uses `GSS_SHIM` on Linux (loads `libgssapi_krb5` via dlopen at runtime)

### 1.5 System.Globalization.Native — ✅ DONE
- [x] `src/native/libs/System.Globalization.Native/BUILD.bazel`
- [x] `src/native/libs/System.Globalization.Native/bazel/config.h` (hardcoded linux-x64)
- [x] Produces `libSystem.Globalization.Native.so` (36 exports, matches CMake)
- [x] Uses `pal_icushim.c` on Linux (loads ICU via dlopen at runtime)

### 1.6 System.Security.Cryptography.Native.OpenSsl — ✅ DONE
- [x] `src/native/libs/System.Security.Cryptography.Native/BUILD.bazel`
- [x] `src/native/libs/System.Security.Cryptography.Native/bazel/linux-glibc-x64/pal_crypto_config.h` (hardcoded linux-x64)
- [x] Produces `libSystem.Security.Cryptography.Native.OpenSsl.so` (379 exports, matches CMake)
- [x] Uses `FEATURE_DISTRO_AGNOSTIC_SSL` on Linux (loads OpenSSL via dlopen at runtime; `opensslshim.h` overrides all `HAVE_OPENSSL_*` to 1, making the binary OpenSSL-version-independent)

### 1.7 Platform-specific libs
- [ ] System.Security.Cryptography.Native.Android (Android only)
- [ ] System.Security.Cryptography.Native.Apple (macOS/iOS/tvOS/Mac Catalyst only)
- ⊘ System.Native.Browser — out of scope (Mono/WASM only)
- ⊘ System.Runtime.InteropServices.JavaScript.Native — out of scope (Mono/WASM only)

---

## 2. Shared Infrastructure (`src/native/`)

### 2.1 minipal — 🔨 linux-x64 done
- [x] `src/native/minipal/BUILD.bazel`
- [x] `src/native/minipal/bazel/linux-glibc-x64/minipalconfig.h` (hardcoded linux-x64)
- [ ] Platform-specific `minipalconfig.h` for other OS/arch combinations (or genrule)

### 2.2 Common headers — 🔨 linux-x64 done
- [x] `src/native/libs/BUILD.bazel` (Common headers)
- [x] `src/native/libs/bazel/linux-glibc-x64/pal_config.h` (hardcoded linux-x64)
- [ ] Platform-specific `pal_config.h` for other OS/arch combinations (or genrule)

### 2.3 Vendored external deps — ✅ DONE
- [x] `src/native/external/zlib-ng/BUILD.bazel`
- [x] `src/native/external/zstd/BUILD.bazel`
- [x] `src/native/external/brotli/BUILD.bazel` (updated for Bazel 9)

### 2.4 containers — ✅ DONE
- [x] `src/native/containers/BUILD.bazel`
  - [x] Two variants: `dn-containers` and `dn-containers-no-lto` (LTO-incompatible scenarios like NativeAOT)
  - [x] `src/native/containers/bazel/linux-glibc-x64/dn-config.h` (hardcoded linux-x64)

### 2.5 eventpipe — ✅ DONE (linux-x64)
- [x] `src/native/eventpipe/BUILD.bazel`
  - [x] `dn-eventpipe-srcs` filegroup (20 source files)
  - [x] `dn-diagnosticserver-srcs` filegroup (8 source files)
  - [x] `dn-diagnosticserver-pal-srcs` filegroup (socket PAL for linux)
  - [x] `eventpipe-headers` cc_library (headers + include paths)
  - [x] Interface library pattern: sources exposed as filegroups for consuming runtimes (CoreCLR, NativeAOT) to compile with their own `ep-rt.h`/`ds-rt.h`
- [x] `src/native/eventpipe/bazel/linux-glibc-x64/ep-shared-config.h` (hardcoded linux-x64)

### 2.6 watchdog
- [ ] `src/native/watchdog/BUILD.bazel`
  - [ ] Small watchdog utility

### 2.7 Vendored external deps (remaining)
- [ ] `src/native/external/llvm-libunwind/BUILD.bazel`
  - [ ] LLVM's libunwind, alternative to GNU libunwind

### 2.8 Bundled GNU libunwind — ✅ DONE (linux-x64)
- [x] `src/native/external/libunwind/BUILD.bazel`
  - [x] `libunwind_generic` cc_library (G-prefix sources: remote/generic unwind)
  - [x] `libunwind_local` cc_library (L-prefix sources: local-only unwind)
  - [x] `libunwind` combined cc_library
  - [x] `bazel/linux-glibc-x64/include/config.h` (hardcoded linux-x64)
  - [x] Generated `libunwind-common.h`, `libunwind.h`, `tdep/libunwind_i.h`

### 2.8 rapidjson — ✅ DONE
- [x] `src/native/external/rapidjson/BUILD.bazel`
  - [x] Header-only JSON library, used by corehost
  - [x] Consumers include via `#include <rapidjson/document.h>` etc.

---

## 3. Core Host (`src/native/corehost/`) — ✅ DONE (linux-x64)

The .NET host (dotnet CLI, apphost, hostfxr, hostpolicy). C++ codebase, 25 CMakeLists.txt files.

### 3.1 hostmisc (static lib) — ✅ DONE
- [x] Platform abstraction (trace, utils, PAL, fx_ver)

### 3.2 libhostcommon (static lib) — ✅ DONE
- [x] JSON parsing, runtime config, bundle support

### 3.3 hostfxr (shared lib) — ✅ DONE
- [x] `libhostfxr.so` (18 exports, matches CMake)
- [x] Version script via genrule from `hostfxr_unixexports.src`

### 3.4 hostpolicy (shared lib) — ✅ DONE
- [x] `libhostpolicy.so` (7 exports, matches CMake)
- [x] Version script via genrule from `hostpolicy_unixexports.src`

### 3.5 dotnet host executable — ✅ DONE
- [x] `dotnet` binary

### 3.6 apphost — ✅ DONE
- [x] `apphost` binary (with `FEATURE_APPHOST` define)

### 3.7 nethost (shared lib) — ✅ DONE
- [x] `libnethost.so` (1 export: `get_hostfxr_path`)

### 3.8 comhost / ijwhost (Windows only)
- [ ] `src/native/corehost/comhost/BUILD.bazel` (Windows COM hosting)
- [ ] `src/native/corehost/ijwhost/BUILD.bazel` (Windows IJW/C++CLI)

### 3.9 apphost/static (single-file host)
- [ ] Depends on CoreCLR being Bazel-built

### 3.10 corehost tests (cross-platform) — ✅ DONE (linux-x64)
- [x] `src/native/corehost/test/BUILD.bazel`
  - [x] `test_fx_ver` executable (framework version parsing tests)
  - [x] `mockcoreclr` shared library (mock CoreCLR)
  - [x] `mockhostfxr_2_2` / `mockhostfxr_5_0` shared libraries (mock hostfxr, two API versions)
  - [x] `mockhostpolicy` shared library (mock hostpolicy)
  - [x] `nativehost` executable (native hosting API tests)
- [ ] Windows-only tests: comsxs, ijw, typelibs (require Windows toolchain)

---

## 4. CoreCLR Runtime (`src/coreclr/`)

The main CLR runtime engine. Large C++ codebase, 86 CMakeLists.txt files.

### 4.1 Common headers and defines — ✅ DONE (linux-x64)
- [x] `src/coreclr/inc/BUILD.bazel`
  - [x] `coreclr_inc` cc_library (161 headers + CORECLR_DEFINES + global copts); depends on all component header targets (binder, debug, dlls, gc, gcdump, hosts, interpreter, jit, md, minipal, pal, vm, eventing, native_inc, native minipal, version_headers)
  - [x] `coreclr_inc_headers_only` lightweight header-only target (no transitive deps, used by PAL)
  - [x] `CORECLR_DEFINES` constant list (~60 defines for linux-x64 retail)
  - [x] `CORECLR_COPTS` constant list (global include paths + warning suppression)

### 4.2 CoreCLR minipal — ✅ DONE (linux-x64)
- [x] `src/coreclr/minipal/BUILD.bazel`
  - [x] `coreclrminipal_headers` (dn-u16.h, dn-stdio.h, minipal.h)
  - [x] `coreclrminipal` (4 C++ sources: doublemapping, dn-u16, dn-stdio, memory)

### 4.3 Native resources — ✅ DONE (linux-x64)
- [x] `src/coreclr/nativeresources/BUILD.bazel`
  - [x] `nativeresourcestring` static lib (resourcestring.cpp)

### 4.4 GC — ✅ DONE (linux-x64)
- [x] `src/coreclr/gc/BUILD.bazel` — `gc_headers` (headers + inlines + defs + env/ + vxsort/ + unix/)
- [x] `gc_pal` cc_library (OBJECT) — Unix PAL (gcenv.unix.cpp, numasupport.cpp, events.cpp, cgroup.cpp)
- [x] `gc_vxsort` cc_library (OBJECT) — Vectorized sorting (AMD64 AVX2/AVX512 sources with `-mavx2`)
- [x] Data descriptor stubs (gc_dll_wks_descriptor, gc_dll_svr_descriptor, gcexp_dll_wks_descriptor, gcexp_dll_svr_descriptor)
- [x] `clrgc` cc_shared_library — Standalone GC with segments (`libclrgc.so`, 2 exports: GC_Initialize, GC_VersionInfo)
- [x] `clrgcexp` cc_shared_library — Standalone GC with regions (`libclrgcexp.so`, 2 exports + USE_REGIONS)
- [x] `src/coreclr/gc/unix/bazel/linux-glibc-x64/config.gc.h` (hardcoded linux-x64)

### 4.5 JIT compiler — ✅ DONE (linux-x64)
- [x] `src/coreclr/jit/BUILD.bazel` — `jit_headers` (headers + hpp + defs + jitstd/)
- [x] `clrjit_static` cc_library (105 .cpp AMD64 sources, compiled as static archive)

### 4.6 VM (execution engine) — ✅ DONE (linux-x64)
- [x] `src/coreclr/vm/BUILD.bazel` — `vm_headers` (headers + hpp + inlines + amd64/ + i386/)
- [x] `cee_wks_asm` cc_library (23 .S assembly files, separate target without PCH)
- [x] `cee_wks_core` cc_library (~220 .cpp VM core sources)
- [x] `cee_wks` cc_library (ceemain.cpp, codeman.cpp, peimagelayout.cpp)
- [x] `src/coreclr/vm/eventing/BUILD.bazel`
  - [x] `eventing_headers` — pre-generated event headers for linux-glibc-x64
  - [x] `eventpipe_gen_srcs` — pre-generated eventpipe C++ sources (5 files)
  - [x] `eventpipe_shim_headers` — CoreCLR-specific eventpipe shim headers
  - [x] `eventpipe` cc_library — native eventpipe/diagnosticserver (unity build .c→C++) + shim + generated sources
- [x] `src/coreclr/vm/datadescriptor/BUILD.bazel`
  - [x] `cdac_contract_descriptor`, `gc_wks_descriptor`, `gc_svr_descriptor` stubs
- [x] `src/coreclr/runtime/BUILD.bazel` — `runtime_headers` + exported .cpp/.S

### 4.7 PAL (Platform Abstraction Layer) — ✅ DONE (linux-x64)
- [x] `src/coreclr/pal/BUILD.bazel`
  - [x] `coreclrpal` static library (~50 C/C++ + 4 assembly files)
  - [x] `tracepointprovider` object library
  - [x] Pre-generated `config.h` for linux-glibc-x64

### 4.8 Binder (assembly loading) — ✅ DONE (linux-x64)
- [x] `src/coreclr/binder/BUILD.bazel` — `binder_headers` (inc/*.h, inc/*.hpp, inc/*.inl)
- [x] `v3binder` cc_library (11 .cpp assembly binder sources)

### 4.9 Metadata (IL metadata reader) — ✅ DONE (linux-x64)
- [x] `src/coreclr/md/BUILD.bazel` — `md_inc` (inc/*.h, *.inl)
- [x] `mdcompiler_wks` cc_library (18 .cpp compiler sources)
- [x] `mdruntime_wks` cc_library (12 .cpp runtime sources)
- [x] `mdruntimerw_wks` cc_library (10 .cpp ENC sources)
- [x] `src/coreclr/md/ceefilegen/BUILD.bazel` — `ceefgen` cc_library (5 .cpp)

### 4.10 Utility code — ✅ DONE (linux-x64)
- [x] `src/coreclr/utilcode/BUILD.bazel` — `utilcode` (OBJECT) + `utilcodestaticnohost` (STATIC)
- [x] `src/coreclr/gcinfo/BUILD.bazel` — `gcinfo` static library
- [x] `src/coreclr/unwinder/BUILD.bazel` — `unwinder_wks` OBJECT library
- [x] `src/coreclr/interop/BUILD.bazel` — `interop` OBJECT library
- [x] `src/coreclr/gcdump/BUILD.bazel` — `gcdump_headers` (headers done, compiled lib pending)
- [x] `src/coreclr/interpreter/BUILD.bazel` — `interpreter_headers` (headers done, compiled lib pending)

### 4.11 Debug support — 🔨 Partial (linux-x64)
- [x] `src/coreclr/debug/BUILD.bazel` — `debug_inc` (inc/ + ee/ + daccess/ + dbgutil/ + di/ headers)
- [x] `debug-pal` cc_library (2 .cpp debug PAL sources)
- [x] `cordbee_wks` cc_library (16 sources — debugger EE, workstation)
- [ ] DAC (`mscordac`) — data access component for debugging/diagnostics
- [ ] DBI (`mscordbi`) — debug interface library
- [ ] dbgutil — debug utility library
- [ ] createdump — crash dump generation tool
- [ ] runtimeinfo — runtime info for debuggers

### 4.12 Hosts & DLLs — ✅ libcoreclr DONE (linux-x64)
- [x] `src/coreclr/hosts/BUILD.bazel` — `hosts_inc` (inc/*.h)
- [x] `src/coreclr/dlls/BUILD.bazel` — `dlls_headers` (**/*.h)
- [x] `src/coreclr/dlls/mscorrc/BUILD.bazel` — `mscorrc` cc_library (pre-generated resource strings, 795 entries)
- [x] `src/coreclr/dlls/mscoree/coreclr/BUILD.bazel` — `libcoreclr.so` (209 MB, 12 exported symbols with V1.0 versioning)
  - [x] Version script from `mscorwks_unixexports.src` (pre-generated `coreclr.exports`)
  - [x] Links all component libraries: VM, JIT, metadata, binder, debug, GC, PAL, eventpipe, etc.
  - [x] 737 total Bazel actions, ~79s clean build
- [x] `src/coreclr/pal/BUILD.bazel` — `eventprovider` cc_library (pre-generated dummy LTTng stubs)
  - [ ] mscordac, mscordbi (diagnostic tooling — pending)

### 4.13 IL Assembler — ✅ DONE (linux-x64)
- [x] `src/coreclr/ilasm/BUILD.bazel` — `ilasm` cc_binary (cfg="exec" tool)
  - [x] Parser includes via textual_hdrs (grammar_before.cpp, grammar_after.cpp)

### 4.14 IL Disassembler — 🔨 Headers only
- [x] `src/coreclr/ildasm/BUILD.bazel` — `ildasm_inc` cc_library (headers only)
- [ ] Full `ildasm` cc_binary

### 4.15 NativeAOT — 🔨 Native runtime only (linux-x64)
- [x] `src/coreclr/nativeaot/BUILD.bazel` — native runtime static libraries
  - [x] nativeaot_runtime_wks, nativeaot_runtime_svr (workstation/server GC variants)
  - [x] standalonegc_disabled, standalonegc_enabled
  - [x] nativeaot_vxsort_enabled, nativeaot_vxsort_disabled
  - [x] bootstrapper, bootstrapperdll, stdc_compat, eventpipe_disabled
  - [x] Per-file copt strips debug defines to avoid REGDISPLAY conflicts
- [ ] ILC compiler (managed C# AOT compiler, `BuildIntegration/`)
- [ ] NativeAOT managed libraries (System.Private.CoreLib, Reflection.Execution, StackTraceMetadata, TypeLoader, Runtime.Base)
- [ ] End-to-end AOT compilation pipeline

### 4.16 Tools — 🔨 Partial
- [x] `src/coreclr/tools/aot/jitinterface/BUILD.bazel` — AOT JIT interface shared library (native C++)
- [ ] SuperPMI — JIT method replay/diff tool (5 native C++ sub-components)
- [ ] SOS — debugging extension (native C++)
- [ ] crossgen2 — ReadyToRun AOT compiler (managed C#)
- [ ] ILC — NativeAOT ahead-of-time compiler (managed C#, ~15 projects)
- [ ] R2RDump — ReadyToRun image dumper (managed C#)


---

## 5. Managed Libraries (`src/libraries/`) — 🔨 In Progress

Managed C# framework assemblies built with `rules_dotnet`. Currently 44 of
~200 library directories have Bazel BUILD files (~156 remaining).
2 libraries have Bazel test BUILD files (out of ~187 with test projects).

### 5.1 System.Private.CoreLib — ✅ DONE
- [x] `src/coreclr/System.Private.CoreLib/BUILD.bazel`
  - [x] `impl_System.Private.CoreLib` — full CoreLib with NativeRuntimeEventSource generator
  - [x] Debug/release feature alignment with C++ side via `//:clr_config` select()
  - [x] `src/libraries/System.Private.CoreLib/src/files.bzl` — source file lists

### 5.2 Framework ref + impl assemblies — 🔨 In Progress
- [x] `src/libraries/BUILD.bazel` — 44 ref/impl assemblies, `impl_netcoreapp` aggregate
- [x] `src/libraries/defs.bzl` — netcoreapp_ref_assembly, netcoreapp_impl_assembly, gen_facades, ref_impl_pair macros
- [x] Source generators: LibraryImportGenerator, Microsoft.Interop.SourceGeneration
- [ ] Remaining ~156 managed framework assemblies

### 5.3 Build Tools — 🔨 Partial
- [x] `src/tools/GenerateResxSource` — resource source generator
- [x] `src/tools/ResGen` — resource compiler
- [x] `src/tools/GenFacades` — type-forward facade generator
- [ ] ILLink / IL trimmer (`src/tools/illink/`) — IL linker, Roslyn analyzers, tasks
- [ ] StressLogAnalyzer (`src/tools/StressLogAnalyzer/`)

### 5.4 Tests — 🔨 Partial
- [x] `src/tests/defs.bzl` — test infrastructure, live_csharp_library, xUnit runner
- [x] `src/tests/live_test.bzl` — `library_test` macro for library unit tests
- [x] 18 test BUILD files (JIT directed tests, common infrastructure)
- [x] 2 library test suites (System.Runtime.Numerics, System.Diagnostics.Contracts)
- [ ] Remaining CoreCLR test suite (~thousands of tests)
- [ ] Remaining library unit tests (~185 libraries)

### 5.5 Installer / Packaging
- [ ] `src/installer/` — runtime packs, NuGet packaging, SDK integration
- [ ] Targeting packs, runtime packs, host packs

---

## 6. Mono Runtime — ⊘ Out of Scope

The Mono runtime (`src/mono/`) is explicitly out of scope for the Bazel build.
Mono-only platforms (Browser/WASM, WASI, Tizen) are also excluded.

---

## 7. Bazel Infrastructure — 🔨 linux-x64 done

- [x] `MODULE.bazel` — Bzlmod workspace, depends on rules_cc@0.2.14, rules_dotnet, bazel_skylib@1.8.2
- [x] `.bazelrc` — Compiler flags matching CMake for linux-x64, per-component config system
- [x] `BUILD.bazel` (root) — Root package, string_flag build settings, config_settings, runtime layout
- [x] `defs.bzl` — Shared macros (csharp_library wrapper, gen_resx_source, resgen)
- [x] `src/libraries/defs.bzl` — Library macros (netcoreapp_ref_assembly, netcoreapp_impl_assembly, gen_facades, ref_impl_pair)
- [x] `src/tests/defs.bzl` — Test infrastructure (live_csharp_library, test runner)
- [x] Compiler flag parity verified against CMake (`-g`, `-O3`, `-std=gnu11`/`-std=c++17`, all warning flags, all defines)
- [x] Clang is the default compiler (matching CMake), with GCC available via `--repo_env=CC=gcc`
- [x] Per-component configuration: `//:clr_config` (debug/checked/release) + `//:libs_config` (debug/release)
- [ ] `.bazelrc` platform configs for other OS/arch targets (e.g., `build:linux-arm64`, `build:macos-x64`)
- [ ] Bazel toolchain definitions for cross-compilation
- [ ] Bazel `select()` rules for platform-conditional source files and defines

---

## 8. Hybrid Runtime Build (`build-bazel-runtime.sh`) — ✅ DONE (linux-x64)

Assembles a working .NET runtime from Bazel-built native components + MSBuild-built managed libraries.

### Usage

```bash
# Full build (managed + native) — first run takes 15-30 min for MSBuild
./build-bazel-runtime.sh

# Per-component configuration (mirrors build.sh -rc / -lc flags)
./build-bazel-runtime.sh -rc checked -lc release      # Checked CLR + release libs
./build-bazel-runtime.sh -rc release                   # Release CLR, debug libs
./build-bazel-runtime.sh -c release                    # Release everything

# Native-only rebuild (fast iteration on C++ changes, ~77s clean)
./build-bazel-runtime.sh --native-only

# Managed-only rebuild
./build-bazel-runtime.sh --managed-only

# Run smoke test after build
./build-bazel-runtime.sh --smoke-test
```

Or use Bazel directly:

```bash
# Build everything, debug (default)
bazel build //...

# Per-component configuration
bazel build --config=clr_checked --config=libs_release //...

# Build just native runtime layout
bazel build //:runtime_native

# Build a specific library
bazel build //src/native/libs/System.Native:System.Native
```

### Output Layout

```
artifacts/bazel-dotnet/
├── dotnet                                          (host executable)
├── host/fxr/11.0.0/libhostfxr.so                  (framework resolver)
└── shared/Microsoft.NETCore.App/11.0.0/
    ├── libcoreclr.so                               (runtime + JIT, statically linked)
    ├── libhostpolicy.so                            (host policy)
    ├── libSystem.Native.so                         (+ 5 other native interop libs)
    ├── System.Private.CoreLib.dll                  (+ ~150 managed framework DLLs)
    └── Microsoft.NETCore.App.deps.json             (framework manifest)
```

### Running an App

```bash
DOTNET_ROOT=artifacts/bazel-dotnet artifacts/bazel-dotnet/dotnet <app.dll>
```

### Build Flow

1. **MSBuild** (one-time, cached): `./build.sh clr.corelib+libs -rc Release -lc Release` → produces System.Private.CoreLib.dll + managed framework DLLs
2. **Bazel** (fast incremental): builds libcoreclr.so, dotnet, hostfxr, hostpolicy, and 6 native interop libs (1,020 actions, ~77s clean)
3. **Assembly**: copies Bazel native outputs + MSBuild managed DLLs into the `dotnet` runtime directory layout

---

## 9. Library Test Tracking

Library tests use the `library_test` macro from `src/tests/live_test.bzl`, which
runs a reflection-based test runner (`LibraryTestRunner.cs`) under `corerun`.
Each library test needs a `tests/BUILD.bazel` file.

**Summary**: 2 of ~187 libraries have Bazel test BUILD files.

### Tiers

Libraries are grouped by implementation complexity:

- **Tier 1** — Source already Bazel-built; just add `tests/BUILD.bazel` (42 libraries)
- **Tier 2** — Self-contained; need source `BUILD.bazel` first, then tests (~57 libraries)
- **Tier 3** — Complex dependency chains; need multiple source builds resolved (~42 libraries)
- **Tier 4** — Platform-specific / Windows-only; deferred until platform support (~23 libraries)

### Tier 1 — Source Bazel-built, add test BUILD only

| Library | Source BUILD | Test BUILD | Notes |
|---------|:-----------:|:----------:|-------|
| Microsoft.Win32.Registry | ✅ | ❌ | Windows-specific tests may need filtering |
| System.Collections | ✅ | ❌ | |
| System.Collections.Concurrent | ✅ | ❌ | |
| System.Collections.Immutable | ✅ | ❌ | |
| System.Collections.NonGeneric | ✅ | ❌ | Needs System.Net.Primitives impl for PlatformDetection |
| System.ComponentModel | ✅ | ❌ | |
| System.Console | ✅ | ❌ | |
| System.Diagnostics.Contracts | ✅ | ✅ | Facade; uses ref_impl_pair; 22 tests pass |
| System.Diagnostics.StackTrace | ✅ | ❌ | |
| System.Diagnostics.Tracing | ✅ | ❌ | |
| System.Formats.Asn1 | ✅ | ❌ | |
| System.IO.FileSystem.AccessControl | ✅ | ❌ | Windows-specific tests |
| System.IO.FileSystem.DriveInfo | ✅ | ❌ | |
| System.IO.IsolatedStorage | ✅ | ❌ | |
| System.IO.MemoryMappedFiles | ✅ | ❌ | |
| System.Linq | ✅ | ❌ | |
| System.Memory | ✅ | ❌ | |
| System.Net.Primitives | ✅ (ref only) | ❌ | Needs impl build; many tests depend on this |
| System.Numerics.Vectors | ✅ | ❌ | |
| System.ObjectModel | ✅ | ❌ | |
| System.Reflection.Emit | ✅ | ❌ | |
| System.Reflection.Emit.ILGeneration | ✅ | ❌ | |
| System.Reflection.Emit.Lightweight | ✅ | ❌ | |
| System.Reflection.Metadata | ✅ | ❌ | |
| System.Reflection.TypeExtensions | ✅ | ❌ | |
| System.Resources.Writer | ✅ | ❌ | |
| System.Runtime.CompilerServices.VisualC | ✅ | ❌ | |
| System.Runtime.InteropServices | ✅ | ❌ | |
| System.Runtime.Intrinsics | ✅ | ❌ | |
| System.Runtime.Loader | ✅ | ❌ | |
| System.Runtime.Numerics | ✅ | ✅ | 176 tests pass (large) |
| System.Runtime.Serialization.Formatters | ✅ | ❌ | |
| System.Security.AccessControl | ✅ | ❌ | Windows-specific tests |
| System.Security.Claims | ✅ | ❌ | |
| System.Security.Cryptography | ✅ | ❌ | |
| System.Security.Principal.Windows | ✅ | ❌ | Windows-specific tests |
| System.Text.Encoding.Extensions | ✅ | ❌ | |
| System.Threading | ✅ | ❌ | |
| System.Threading.Overlapped | ✅ | ❌ | |
| System.Threading.Tasks.Parallel | ✅ | ❌ | |
| System.Threading.Thread | ✅ | ❌ | |
| System.Threading.ThreadPool | ✅ | ❌ | |

### Tier 2 — Self-contained, need source BUILD first

| Library | Notes |
|---------|-------|
| Microsoft.Bcl.AsyncInterfaces | |
| Microsoft.Bcl.Memory | |
| Microsoft.Bcl.Numerics | |
| Microsoft.Bcl.TimeProvider | |
| Microsoft.CSharp | |
| Microsoft.Extensions.Primitives | |
| Microsoft.Win32.Primitives | |
| System.CodeDom | |
| System.Collections.Specialized | |
| System.ComponentModel.Annotations | |
| System.ComponentModel.EventBasedAsync | |
| System.ComponentModel.Primitives | |
| System.Diagnostics.DiagnosticSource | |
| System.Diagnostics.FileVersionInfo | |
| System.Diagnostics.TextWriterTraceListener | |
| System.Diagnostics.TraceSource | |
| System.Drawing.Primitives | |
| System.Formats.Cbor | |
| System.Formats.Nrbf | |
| System.Formats.Tar | |
| System.IO.Compression | |
| System.IO.Compression.Brotli | |
| System.IO.Compression.ZipFile | |
| System.IO.FileSystem.Watcher | |
| System.IO.Hashing | |
| System.IO.Pipelines | |
| System.IO.Pipes | |
| System.Linq.AsyncEnumerable | |
| System.Linq.Expressions | |
| System.Linq.Parallel | |
| System.Linq.Queryable | |
| System.Memory.Data | |
| System.Net.ServerSentEvents | |
| System.Net.WebHeaderCollection | |
| System.Net.WebProxy | |
| System.Private.Uri | |
| System.Reflection.Context | |
| System.Reflection.DispatchProxy | |
| System.Reflection.Extensions | |
| System.Reflection.MetadataLoadContext | |
| System.Resources.Extensions | |
| System.Runtime | |
| System.Runtime.Serialization.Primitives | |
| System.Security.Cryptography.Cose | |
| System.Security.Cryptography.OpenSsl | |
| System.Security.Cryptography.Pkcs | |
| System.Security.Cryptography.Xml | |
| System.ServiceModel.Syndication | |
| System.Text.Encoding.CodePages | |
| System.Text.Encodings.Web | |
| System.Text.Json | |
| System.Text.RegularExpressions | |
| System.Threading.Channels | |
| System.Threading.RateLimiting | |
| System.Threading.Tasks.Dataflow | |
| System.Transactions.Local | |
| System.Web.HttpUtility | |

### Tier 3 — Complex dependency chains

| Library | Notes |
|---------|-------|
| Microsoft.Bcl.Cryptography | |
| Microsoft.Extensions.Caching.Memory | + Abstractions |
| Microsoft.Extensions.Configuration | 8 sub-libraries |
| Microsoft.Extensions.DependencyInjection | + Abstractions |
| Microsoft.Extensions.DependencyModel | |
| Microsoft.Extensions.Diagnostics | + Abstractions |
| Microsoft.Extensions.FileProviders.Composite | + Abstractions, Physical |
| Microsoft.Extensions.FileSystemGlobbing | |
| Microsoft.Extensions.HostFactoryResolver | |
| Microsoft.Extensions.Hosting | + Abstractions, Systemd |
| Microsoft.Extensions.Http | |
| Microsoft.Extensions.Logging | 7 sub-libraries |
| Microsoft.Extensions.Options | + ConfigurationExtensions, DataAnnotations |
| Microsoft.VisualBasic.Core | |
| System.ComponentModel.Composition | + Registration |
| System.ComponentModel.TypeConverter | |
| System.Composition.* | 5 sub-libraries |
| System.Configuration.ConfigurationManager | |
| System.Data.Common | |
| System.Diagnostics.Process | |
| System.IO.Packaging | |
| System.Net.Http | + Json, WinHttpHandler |
| System.Net.HttpListener | |
| System.Net.Mail | |
| System.Net.NameResolution | |
| System.Net.NetworkInformation | |
| System.Net.Ping | |
| System.Net.Requests | |
| System.Net.Security | |
| System.Net.Sockets | |
| System.Net.WebClient | |
| System.Net.WebSockets | + Client |
| System.Numerics.Tensors | |
| System.Private.DataContractSerialization | |
| System.Private.Xml | + Linq |
| System.Runtime.Caching | |
| System.Runtime.Serialization.Json | |
| System.Runtime.Serialization.Schema | |
| System.Runtime.Serialization.Xml | |
| System.Xml.ReaderWriter | |
| System.Xml.XDocument | |
| System.Xml.XPath | + XDocument |
| System.Xml.XmlSerializer | |

### Tier 4 — Platform-specific / Windows-only (deferred)

| Library | Platform |
|---------|----------|
| Microsoft.Extensions.Hosting.WindowsServices | Windows |
| Microsoft.Win32.Registry.AccessControl | Windows |
| Microsoft.Win32.SystemEvents | Windows |
| Microsoft.XmlSerializer.Generator | Windows |
| System.Data.Odbc | Windows |
| System.Data.OleDb | Windows |
| System.Diagnostics.EventLog | Windows |
| System.Diagnostics.PerformanceCounter | Windows |
| System.DirectoryServices | Windows |
| System.DirectoryServices.AccountManagement | Windows |
| System.DirectoryServices.Protocols | Windows |
| System.IO.Pipes.AccessControl | Windows |
| System.IO.Ports | Windows |
| System.Management | Windows |
| System.Net.Quic | Windows |
| System.Runtime.InteropServices.JavaScript | Browser/WASM |
| System.Security.Cryptography.Cng | Windows |
| System.Security.Cryptography.Csp | Windows |
| System.Security.Cryptography.ProtectedData | Windows |
| System.Security.Permissions | Windows |
| System.ServiceProcess.ServiceController | Windows |
| System.Speech | Windows |
| System.Threading.AccessControl | Windows |
| System.Windows.Extensions | Windows |

---

## Notes

- **No CMake files are modified or deleted** — Bazel files are purely additive
- Config headers (`pal_config.h`, `minipalconfig.h`, `config.h`, `pal_crypto_config.h`) live in platform-specific subdirectories under `bazel/` (e.g., `bazel/linux-glibc-x64/`). The directory name encodes the relevant dimensions (OS, libc, arch). Multi-platform support will add sibling directories and `select()` rules to pick the right one.
- Platform-specific libraries (Browser, Android, Apple) each need their own platform toolchains before they can be ported
- Clang is the default compiler (matching CMake). Override with `--repo_env=CC=gcc` if needed.
- Build commands (linux-x64):
  - **Full runtime (hybrid)**: `./build-bazel-runtime.sh` (or `--native-only` for fast C++ iteration)
  - **Everything**: `bazel build //...`
  - Native libs: `bazel build //src/native/libs/System.Native:System.Native //src/native/libs/System.IO.Compression.Native:System.IO.Compression.Native //src/native/libs/System.IO.Ports.Native:System.IO.Ports.Native //src/native/libs/System.Net.Security.Native:System.Net.Security.Native //src/native/libs/System.Globalization.Native:System.Globalization.Native //src/native/libs/System.Security.Cryptography.Native:System.Security.Cryptography.Native.OpenSsl`
  - Corehost: `bazel build //src/native/corehost:hostfxr //src/native/corehost:hostpolicy //src/native/corehost:dotnet //src/native/corehost:apphost //src/native/corehost:nethost`
  - **libcoreclr.so**: `bazel build //src/coreclr/dlls/mscoree/coreclr:libcoreclr.so`
  - **Managed libs**: `bazel build //src/libraries:impl_netcoreapp`
  - **Runtime layout**: `bazel build //:runtime_native`
  - **Core_Root (test runtime)**: `bazel build //:Core_Root`
