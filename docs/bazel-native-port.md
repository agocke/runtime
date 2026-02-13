# Plan: Port All Native C/C++ Code to Bazel

## Problem Statement

Add a Bazel build alongside the existing CMake build for all native C/C++ code in dotnet/runtime. The goal is to eventually support all platforms CMake currently compiles for. The Bazel build must produce the same output binaries as CMake.

## Overall Status

Bazel infrastructure is in place: `MODULE.bazel`, `.bazelrc` (with compiler flags matching CMake), root `BUILD.bazel`. Compiler flags have been verified identical to CMake for linux-x64.

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
- [ ] `src/native/external/libunwind/BUILD.bazel`
  - [ ] Used by coreclr for stack unwinding
- [ ] `src/native/external/llvm-libunwind/BUILD.bazel`
  - [ ] LLVM's libunwind, alternative to GNU libunwind

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

### 4.1 GC
- [ ] `src/coreclr/gc/BUILD.bazel`
  - [ ] `src/coreclr/gc/unix/BUILD.bazel` (PAL for unix)
  - [ ] `src/coreclr/gc/vxsort/BUILD.bazel` (vectorized sort)

### 4.2 JIT compiler
- [ ] `src/coreclr/jit/BUILD.bazel`
  - [ ] `src/coreclr/jit/static/BUILD.bazel`

### 4.3 VM (execution engine)
- [ ] `src/coreclr/vm/BUILD.bazel`
  - [ ] `src/coreclr/vm/wks/BUILD.bazel` (workstation GC variant)
  - [ ] `src/coreclr/vm/eventing/BUILD.bazel` (EventPipe integration)

### 4.4 PAL (Platform Abstraction Layer)
- [ ] `src/coreclr/pal/BUILD.bazel`
  - [ ] Large POSIX compatibility layer (~50 source files)

### 4.5 Binder (assembly loading)
- [ ] `src/coreclr/binder/BUILD.bazel`

### 4.6 Metadata (IL metadata reader)
- [ ] `src/coreclr/md/BUILD.bazel`

### 4.7 Utility code
- [ ] `src/coreclr/utilcode/BUILD.bazel`
- [ ] `src/coreclr/gcinfo/BUILD.bazel`
- [ ] `src/coreclr/gcdump/BUILD.bazel`
- [ ] `src/coreclr/unwinder/BUILD.bazel`
- [ ] `src/coreclr/interop/BUILD.bazel`
- [ ] `src/coreclr/interpreter/BUILD.bazel`

### 4.8 Debug support
- [ ] `src/coreclr/debug/BUILD.bazel` (debugger, diagnostics, DAC)

### 4.9 Hosts & DLLs
- [ ] `src/coreclr/hosts/BUILD.bazel` (coreclr host)
- [ ] `src/coreclr/dlls/BUILD.bazel` (mscoree, mscordac, mscordbi)

### 4.10 IL Assembler / Disassembler
- [ ] `src/coreclr/ilasm/BUILD.bazel`
- [ ] `src/coreclr/ildasm/BUILD.bazel`

### 4.11 NativeAOT
- [ ] `src/coreclr/nativeaot/BUILD.bazel`
  - [ ] `src/coreclr/nativeaot/Runtime/BUILD.bazel`
  - [ ] `src/coreclr/nativeaot/Bootstrap/BUILD.bazel`

### 4.12 Tools
- [ ] `src/coreclr/tools/BUILD.bazel`
  - [ ] `src/coreclr/tools/aot/jitinterface/BUILD.bazel`
  - [ ] `src/coreclr/tools/superpmi/BUILD.bazel` (5 sub-components)

### 4.13 Inc / nativeresources
- [ ] `src/coreclr/inc/BUILD.bazel` (headers)
- [ ] `src/coreclr/nativeresources/BUILD.bazel`

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

## Notes

- **No CMake files are modified or deleted** — Bazel files are purely additive
- Config headers (`pal_config.h`, `minipalconfig.h`, `config.h`, `pal_crypto_config.h`) live in platform-specific subdirectories under `bazel/` (e.g., `bazel/linux-glibc-x64/`). The directory name encodes the relevant dimensions (OS, libc, arch). Multi-platform support will add sibling directories and `select()` rules to pick the right one.
- CoreCLR is the largest component (~86 CMakeLists.txt) and will require the most effort
- Platform-specific libraries (Browser, Android, Apple) each need their own platform toolchains before they can be ported
- Build command (linux-x64): `bazel --nohome_rc build //src/native/libs/System.Native:System.Native //src/native/libs/System.IO.Compression.Native:System.IO.Compression.Native //src/native/libs/System.IO.Ports.Native:System.IO.Ports.Native //src/native/libs/System.Net.Security.Native:System.Net.Security.Native //src/native/libs/System.Globalization.Native:System.Globalization.Native //src/native/libs/System.Security.Cryptography.Native:System.Security.Cryptography.Native.OpenSsl //src/native/corehost:hostfxr //src/native/corehost:hostpolicy //src/native/corehost:dotnet //src/native/corehost:apphost //src/native/corehost:nethost`
