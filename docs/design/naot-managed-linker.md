# NativeAOT Managed Linker: Eliminating External Toolchain Dependencies

## Problem Statement

Today, `dotnet publish` for NativeAOT requires a platform C/C++ toolchain (clang/gcc
on Linux, link.exe/MSVC on Windows, Xcode on macOS). The NativeAOT compiler (ILC)
emits a single relocatable object file (`.o`/`.obj`), then invokes the **system linker**
(via MSBuild targets) to combine it with pre-compiled static libraries (GC, bootstrapper,
native interop, etc.) into a final executable.

This external linker dependency:
- Requires users to install platform-specific toolchains
- Prevents true cross-compilation (e.g., building a Linux binary on macOS)
- Adds friction to the developer experience compared to `go build`

## Inspiration: How Go Does It

Go's toolchain produces fully standalone executables without invoking any system tools:

- **Go's compiler and linker are written in Go.** The linker directly emits ELF, Mach-O,
  and PE executables — it never invokes `ld`, `link.exe`, or `clang`.
- **On Linux,** Go makes direct syscalls (no libc). Binaries are fully static.
- **On macOS,** Go's linker emits Mach-O binaries with `LC_LOAD_DYLIB` entries referencing
  `libSystem.B.dylib`. Symbol names are hardcoded; dyld resolves them at process startup.
  No macOS SDK is needed at build time.
- **Cross-compilation** is trivial: `GOOS=linux GOARCH=arm64 go build` works from any host.

Go can do this because its runtime is written entirely in Go and assembly — no C/C++
dependencies. NativeAOT has C++ components (GC, PAL), but this is a build-time concern,
not a publish-time one: those components are pre-compiled into static libraries and
shipped in the SDK.

## Architecture Overview

### Current Pipeline

```
User runs: dotnet publish -r linux-x64

ILC Compiler          System Linker (clang/ld)         Final Output
─────────────         ────────────────────────         ────────────
IL + metadata    →    app.o                       ┐
                      libRuntime.WorkstationGC.a  │
                      libeventpipe-enabled.a      ├→   myapp (ELF executable)
                      libbootstrapper.o           │
                      libaotminipal.a             │
                      System.Native.a             ┘
```

### Proposed Pipeline

```
User runs: dotnet publish -r linux-x64

ILC Compiler + Managed Linker                          Final Output
─────────────────────────────                          ────────────
IL + metadata    →    (in-memory sections)         ┐
                      libRuntime.WorkstationGC.a   ├→   myapp (ELF executable)
                      libeventpipe-enabled.a       │
                      libbootstrapper.o            │
                      libaotminipal.a              │
                      System.Native.a              ┘
```

The system linker is replaced by extending ILC's existing ObjectWriter infrastructure
to also **read** pre-compiled objects and **emit executables** rather than relocatable
objects.

## Dependency Layers

Eliminating the external toolchain involves five layers. The managed linker (Layer 1)
is the primary deliverable. Layers 2–5 are future work that further reduce runtime
dependencies.

### Layer 1: Managed Linker (This Proposal)

Replace the system linker invocation with a managed linker inside ILC. The managed
linker reads the pre-compiled `.a`/`.o` files from the SDK, combines them with ILC's
compiler-generated sections, resolves symbols, applies relocations, and emits a final
executable.

**This is the only layer needed to eliminate the external toolchain requirement.**

### Layer 2: Custom CRT Startup (Future)

Replace `__libc_start_main` with a custom `_start` in assembly. NativeAOT's
`main.cpp` is already minimal (calls `RhInitialize` → `ThreadEntryPoint`). A custom
entry point would set up the stack from the kernel-provided `argc/argv/envp` and jump
directly to runtime init.

### Layer 3: Direct Syscalls (Future, Linux Only)

Replace libc syscall wrappers (`mmap`, `mprotect`, `write`, `clock_gettime`, etc.)
in the PAL with direct `syscall` instructions. The PAL (`PalUnix.cpp`, ~54 functions)
is already a thin abstraction layer. On macOS, libSystem remains required (Apple's
only stable ABI).

### Layer 4: Custom Threading Primitives (Future, Linux Only)

Replace pthreads with `clone3` + `futex`-based implementations. NativeAOT already
has custom allocators (`allocheap.cpp`), so `malloc`/`free` are mostly not needed.

### Layer 5: Dynamic Loading Without libc (Future)

P/Invoke targets (OpenSSL, ICU) require dynamic library loading at runtime. Options:

- **`PT_INTERP` approach (recommended):** Emit `PT_INTERP` pointing at `ld-linux.so`
  in the ELF executable. The kernel loads the dynamic linker, which makes `dlopen`/
  `dlsym` available, then transfers control to our `_start`. The binary itself links
  no libc, but the dynamic linker is available for P/Invoke resolution.
- **Minimal managed ELF loader:** Implement `dlopen`/`dlsym` in managed code using
  direct `mmap` syscalls + ELF parsing. Complex due to transitive dependencies
  (OpenSSL itself links libc).

## Managed Linker Design

### Scope

The managed linker is intentionally narrow in scope compared to general-purpose
linkers like LLD (~21k LOC) or gold (~198k LOC):

| Feature | LLD | Managed Linker |
|---------|-----|----------------|
| Arbitrary input objects | ✅ | ❌ Fixed SDK inputs + one ILC output |
| Linker scripts | ✅ | ❌ Not needed |
| Link-Time Optimization | ✅ | ❌ Not needed |
| Shared library output | ✅ | ❌ Executables only (initially) |
| Symbol versioning | ✅ | ❌ Not needed |
| `--gc-sections` | ✅ | ❌ ILC already does dead code elimination |
| Archive member selection | ✅ | ✅ Must select needed `.o` members from `.a` |
| Section merging | ✅ | ✅ |
| Relocation processing | ✅ | ✅ Subset of types |
| COMDAT/group handling | ✅ | ✅ |
| Debug info passthrough | ✅ | ✅ DWARF / CodeView |

### Existing Code to Build On

NativeAOT's ObjectWriter already contains significant infrastructure:

| File | Lines | Contains |
|------|-------|----------|
| `ElfObjectWriter.cs` | 1,186 | ELF `.o` emission — sections, symbols, relocations |
| `MachObjectWriter.cs` | 1,214 | Mach-O `.o` emission |
| `CoffObjectWriter.cs` | 1,140 | COFF `.obj` emission |
| `ElfNative.cs` | 619 | ELF struct definitions (headers, sections, symbols, all relocation types for x86, x64, ARM32, ARM64, RISC-V, LoongArch) |
| `MachNative.cs` | 137 | Mach-O struct definitions |
| `UnixObjectWriter.cs` | 352 | DWARF, `.eh_frame`, LSDA generation |
| `ObjectWriter.cs` | 616 | Abstract base, section/symbol management |

**Key insight:** `ElfNative.cs` already defines all ELF structures and relocation type
enums. The writer already knows how to produce every section type. The new code needs
to **read** the same formats and **emit executable** rather than relocatable output.

### Components

#### 1. Archive Reader (`ElfArchiveReader.cs`, ~400 LOC)

Read `.a` (ar format) archives:
- Parse the `!<arch>\n` header and member headers
- Extract individual `.o` members on demand
- Handle the archive symbol table (`/` member) for efficient member selection
- Select only archive members that define symbols referenced by other inputs

#### 2. Object Reader (`ElfObjectReader.cs`, ~800 LOC)

Parse ELF relocatable objects (`.o` files):
- Read ELF header, section headers, symbol table, string table
- Identify allocatable sections (`.text`, `.data`, `.rodata`, `.bss`)
- Read relocation entries (`.rela.*` / `.rel.*`)
- Handle COMDAT groups (`SHT_GROUP` / `SHF_GROUP`)
- Reuse struct definitions from `ElfNative.cs`

#### 3. Symbol Resolver (`SymbolResolver.cs`, ~600 LOC)

Build a global symbol table from all inputs:
- Collect all global/weak symbol definitions
- Match undefined references to definitions
- Handle weak symbol semantics (weak loses to global)
- Handle COMDAT deduplication (keep first, discard duplicates)
- Report unresolved symbols with diagnostics

#### 4. Section Layout Engine (`SectionLayout.cs`, ~500 LOC)

Compute the final executable layout:
- Merge input sections by name/type (all `.text` → one `.text` segment)
- Respect alignment requirements from input section headers
- Assign virtual addresses to all sections
- Create program headers (`PT_LOAD`, `PT_INTERP`, `PT_GNU_STACK`, etc.)
- Compute file offsets for all segments

#### 5. Relocation Processor (`RelocationProcessor.cs`, ~800 LOC)

Apply relocations to produce the final executable content:
- Process relocations from all input objects
- Map input symbols to final virtual addresses via the symbol resolver
- Apply architecture-specific relocation formulas

Relocation types actually needed (subset of what `ElfNative.cs` defines):

**x64 (primary target):**
- `R_X86_64_64` — absolute 64-bit
- `R_X86_64_PC32` — PC-relative 32-bit
- `R_X86_64_PLT32` — PLT-relative (treat as PC32 for static link)
- `R_X86_64_32` / `R_X86_64_32S` — absolute 32-bit
- `R_X86_64_GOTPCREL` — only if GOT entries needed

**ARM64:**
- `R_AARCH64_CALL26` / `R_AARCH64_JUMP26` — branch
- `R_AARCH64_ABS64` — absolute 64-bit
- `R_AARCH64_ADD_ABS_LO12_NC` / `R_AARCH64_ADR_PREL_PG_HI21` — page-relative
- `R_AARCH64_LDST*_ABS_LO12_NC` — load/store offset

#### 6. Executable Emitter (`ElfExecutableWriter.cs`, ~600 LOC)

Extends or parallels `ElfObjectWriter` to emit executable ELF:
- Write ELF header with `ET_EXEC` (or `ET_DYN` for PIE) instead of `ET_REL`
- Write program headers (PHDRs) instead of just section headers
- Emit `PT_INTERP` segment pointing to `/lib64/ld-linux-x86-64.so.2` (for `dlopen` support)
- Write merged section content with relocations applied
- Write `.dynamic` section if dynamic linking entries needed
- Set entry point address (`e_entry`) to `_start` / bootstrapper entry

#### Mach-O and COFF Equivalents

Parallel implementations for macOS (Mach-O) and Windows (COFF/PE) following the same
component structure. Mach-O needs `LC_LOAD_DYLIB` for libSystem; COFF/PE needs import
directory entries for kernel32, etc.

### Estimated Size

| Component | ELF LOC | Mach-O LOC | COFF LOC |
|-----------|---------|------------|----------|
| Archive Reader | 400 | 400 (shared) | 400 (shared format) |
| Object Reader | 800 | 700 | 700 |
| Symbol Resolver | 600 | 600 (shared) | 600 (shared) |
| Section Layout | 500 | 500 | 500 |
| Relocation Processor | 800 | 700 | 600 |
| Executable Emitter | 600 | 700 | 700 |
| **Total** | **~3,700** | **~3,600** | **~3,500** |

Shared components (archive reader, symbol resolver) reduce the total to roughly
**~7,000–8,000 LOC** for all three platforms, comparable to the existing ObjectWriter
code (~5,800 LOC).

### Static Libraries to Link

From the NativeAOT SDK build integration targets, the inputs are:

**Linux (ELF):**
```
libbootstrapper.o                    — entry point (_start / main)
libRuntime.WorkstationGC.a           — GC + core runtime (or ServerGC variant)
libeventpipe-enabled.a               — diagnostics (or disabled variant)
libRuntime.VxsortEnabled.a           — vectorized sorting (x64 only)
libaotminipal.a                      — minimal platform abstraction
libstdc++compat.a                    — C++ runtime shims
libz.a                               — zlib compression
libbrotlicommon.a                    — brotli compression
libbrotlienc.a
libbrotlidec.a
System.Native.a                      — native interop for System.*
System.Globalization.Native.a
System.IO.Compression.Native.a
System.Net.Security.Native.a
System.Security.Cryptography.Native.OpenSsl.a
```

**macOS (Mach-O):** Same set, Mach-O `.o`/`.a` format.

**Windows (COFF/PE):** `.obj`/`.lib` equivalents plus `kernel32.lib`, `advapi32.lib`,
`ntdll.lib`, etc. (import libraries for Windows system DLLs).

## Implementation Phases

### Phase 1: ELF Executable Linking (linux-x64)

Implement the managed linker for the most common NativeAOT target:
- Archive/object reader for ELF
- Symbol resolution
- Section layout for x64
- x64 relocation processing (~5 relocation types)
- ELF executable emission with `PT_INTERP`
- Integration into ILC as `--link-mode:managed` option
- Validate: produce a working "Hello World" NativeAOT binary without clang/ld

### Phase 2: ARM64 Support + Hardening

- ARM64 relocation processing
- Comprehensive testing with real-world apps (ASP.NET, console apps)
- Debug info (DWARF) passthrough
- Error diagnostics comparable to system linker

### Phase 3: Mach-O Executable Linking (osx-x64, osx-arm64)

- Mach-O object/archive reader
- Mach-O executable emission with `LC_LOAD_DYLIB` for libSystem
- Code signing (ad-hoc) for Apple Silicon

### Phase 4: PE/COFF Executable Linking (win-x64)

- COFF object/archive reader
- PE executable emission with import directory for system DLLs
- Subsystem configuration (console/windows)

### Phase 5: Default Integration

- Make managed linking the default (remove clang/link.exe requirement)
- Enable true cross-compilation scenarios
- Remove `CppLinker` detection and MSBuild targets for external linker

## Open Questions

1. **Position-Independent Executables (PIE):** Should the managed linker emit `ET_DYN`
   (PIE) or `ET_EXEC`? Modern distros prefer PIE for ASLR. PIE requires GOT/PLT
   handling, adding complexity.

2. **Debug Info:** Should the linker process DWARF relocations (needed for debuggers
   to work), or initially emit binaries without debug info?

3. **Shared Library Output:** NativeAOT supports `NativeLib=Shared` (`.so`/`.dylib`
   output). Should the managed linker support this from the start, or only executables?

4. **`PT_INTERP` vs. Fully Static:** For Layer 5 (future), should we emit `PT_INTERP`
   to enable `dlopen`, or pursue a fully static binary model? `PT_INTERP` is simpler
   and more compatible.

5. **Windows Import Libraries:** Windows linking requires parsing `.lib` import
   libraries (which are archives of small COFF objects with `IMAGE_IMPORT_DESCRIPTOR`
   entries). This is well-defined but adds scope to Phase 4.
