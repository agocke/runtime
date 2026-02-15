# Plan: Port All Native C/C++ Code to Bazel

## Problem Statement

Add a Bazel build alongside the existing CMake build for all native C/C++ code in dotnet/runtime. The goal is to eventually support all platforms CMake currently compiles for. The Bazel build must produce the same output binaries as CMake.

## Verifying Bazel ↔ CMake Equivalence

`compare-bazel-cmake.sh` automates binary comparison between Bazel and CMake outputs. It compares all 10 native binaries on:

- **Exported symbols** (`nm -D`, stripping version tags for fair comparison)
- **Dynamic dependencies** (`NEEDED` entries)
- **SONAME**
- **ELF section layout** (filtering strip-related sections like `.gnu_debuglink`)
- **Stripped file size** (strips both to temp files for fair comparison)

```bash
# Prerequisites: build both systems first
./build.sh clr+libs+host                              # CMake
bazel --nohome_rc build //:runtime_native              # Bazel

# Run comparison
./compare-bazel-cmake.sh                               # default (debug)
./compare-bazel-cmake.sh --config release              # release mode
./compare-bazel-cmake.sh --verbose                     # show diffs
./compare-bazel-cmake.sh --section-tolerance 10        # allow 10% size variance
./compare-bazel-cmake.sh --build                       # build both first, then compare
```

## Overall Status

All native C/C++ components needed for a working .NET runtime on linux-x64 are now building with Bazel: CoreCLR (`libcoreclr.so` with statically-linked JIT), corehost (`dotnet`, `libhostfxr.so`, `libhostpolicy.so`), and all 6 native interop libraries. A hybrid build script (`build-bazel-runtime.sh`) assembles Bazel-built native components with MSBuild-built managed C# libraries into a functional `dotnet` runtime layout. Total: 1,020 Bazel actions, ~77s clean build for all native targets.

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
| Browser (Emscripten/WASM) | ✅ | ❌ Not started | Mono/WASM only |
| WASI | ✅ | ❌ Not started | |
| Tizen | ✅ | ❌ Not started | |

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
| WASM | ✅ | ❌ Not started | Browser/WASI only |

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
- [ ] System.Native.Browser (Browser/WASM only)
- [ ] System.Runtime.InteropServices.JavaScript.Native (Browser/WASM only)
- [ ] System.Security.Cryptography.Native.Android (Android only)
- [ ] System.Security.Cryptography.Native.Apple (macOS/iOS/tvOS/Mac Catalyst only)

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
  - [x] Interface library pattern: sources exposed as filegroups for consuming runtimes (CoreCLR, Mono, NativeAOT) to compile with their own `ep-rt.h`/`ds-rt.h`
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

### 4.11 Debug support — ✅ DONE (linux-x64)
- [x] `src/coreclr/debug/BUILD.bazel` — `debug_inc` (inc/ + ee/ + daccess/ + dbgutil/ + di/ headers)
- [x] `debug-pal` cc_library (2 .cpp debug PAL sources)
- [x] `cordbee_wks` cc_library (16 sources — debugger EE, workstation)
  - [ ] DAC, DBI, dbgutil (diagnostic tooling — pending)

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

### 4.13 IL Assembler / Disassembler
- [ ] `src/coreclr/ilasm/BUILD.bazel`
- [ ] `src/coreclr/ildasm/BUILD.bazel`

### 4.14 NativeAOT
- [ ] `src/coreclr/nativeaot/BUILD.bazel`
  - [ ] `src/coreclr/nativeaot/Runtime/BUILD.bazel`
  - [ ] `src/coreclr/nativeaot/Bootstrap/BUILD.bazel`

### 4.15 Tools
- [ ] `src/coreclr/tools/BUILD.bazel`
  - [ ] `src/coreclr/tools/aot/jitinterface/BUILD.bazel`
  - [ ] `src/coreclr/tools/superpmi/BUILD.bazel` (5 sub-components)

---

## 5. Mono Runtime (`src/mono/`)

The Mono runtime engine. 13 CMakeLists.txt files.

- [ ] `src/mono/mono/BUILD.bazel`
  - [ ] Core Mono runtime (mini, metadata, utils, eglib, sgen)
  - [ ] Mono CMake structure under `src/mono/cmake/`
  - [ ] Depends on `src/mono/mono/` sources

---

## 6. Bazel Infrastructure — 🔨 linux-x64 done

- [x] `MODULE.bazel` — Bzlmod workspace, depends on rules_cc@0.2.14
- [x] `.bazelrc` — Compiler flags matching CMake for linux-x64
- [x] `BUILD.bazel` (root) — Root package file
- [x] Compiler flag parity verified against CMake (`-g`, `-O3`, `-std=gnu11`/`-std=c++17`, all warning flags, all defines)
- [ ] `.bazelrc` platform configs for other OS/arch targets (e.g., `build:linux-arm64`, `build:macos-x64`)
- [ ] Bazel toolchain definitions for cross-compilation
- [ ] Bazel `select()` rules for platform-conditional source files and defines

---

## 7. Hybrid Runtime Build (`build-bazel-runtime.sh`) — ✅ DONE (linux-x64)

Assembles a working .NET runtime from Bazel-built native components + MSBuild-built managed libraries.

### Usage

```bash
# Full build (managed + native) — first run takes 15-30 min for MSBuild, subsequent runs use cached artifacts
./build-bazel-runtime.sh

# Native-only rebuild (fast iteration on C++ changes, ~77s clean)
./build-bazel-runtime.sh --native-only

# Managed-only rebuild
./build-bazel-runtime.sh --managed-only

# Force managed rebuild even if artifacts exist
./build-bazel-runtime.sh --rebuild-managed

# Debug configuration
./build-bazel-runtime.sh --config debug

# Run smoke test after build
./build-bazel-runtime.sh --smoke-test
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

## Notes

- **No CMake files are modified or deleted** — Bazel files are purely additive
- Config headers (`pal_config.h`, `minipalconfig.h`, `config.h`, `pal_crypto_config.h`) live in platform-specific subdirectories under `bazel/` (e.g., `bazel/linux-glibc-x64/`). The directory name encodes the relevant dimensions (OS, libc, arch). Multi-platform support will add sibling directories and `select()` rules to pick the right one.
- CoreCLR is the largest component (~86 CMakeLists.txt) and will require the most effort
- Platform-specific libraries (Browser, Android, Apple) each need their own platform toolchains before they can be ported
- Build commands (linux-x64):
  - **Full runtime (hybrid)**: `./build-bazel-runtime.sh` (or `--native-only` for fast C++ iteration)
  - Native libs: `bazel --nohome_rc build //src/native/libs/System.Native:System.Native //src/native/libs/System.IO.Compression.Native:System.IO.Compression.Native //src/native/libs/System.IO.Ports.Native:System.IO.Ports.Native //src/native/libs/System.Net.Security.Native:System.Net.Security.Native //src/native/libs/System.Globalization.Native:System.Globalization.Native //src/native/libs/System.Security.Cryptography.Native:System.Security.Cryptography.Native.OpenSsl`
  - Corehost: `bazel --nohome_rc build //src/native/corehost:hostfxr //src/native/corehost:hostpolicy //src/native/corehost:dotnet //src/native/corehost:apphost //src/native/corehost:nethost`
  - CoreCLR foundation: `bazel --nohome_rc build //src/coreclr/pal:coreclrpal //src/coreclr/utilcode //src/coreclr/utilcode:utilcodestaticnohost //src/coreclr/gcinfo //src/coreclr/unwinder:unwinder_wks //src/coreclr/interop //src/coreclr/nativeresources:nativeresourcestring //src/coreclr/pal:tracepointprovider`
  - CoreCLR GC: `bazel --nohome_rc build //src/coreclr/gc:clrgc //src/coreclr/gc:clrgcexp`
  - **libcoreclr.so**: `bazel --nohome_rc build //src/coreclr/dlls/mscoree/coreclr:libcoreclr.so`
