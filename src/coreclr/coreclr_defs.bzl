# Shared constants for CoreCLR Bazel builds (linux-x64).
# Import into BUILD.bazel files with:
#   load("//src/coreclr:coreclr_defs.bzl", "CORECLR_DEFINES", "CORECLR_COPTS")

# --- Feature defines for linux-x64 retail ---
# Derived from clrdefinitions.cmake and clrfeatures.cmake.

CORECLR_DEFINES = [
    # Platform
    "HOST_64BIT",
    "HOST_AMD64",
    "HOST_UNIX",
    "TARGET_64BIT",
    "TARGET_AMD64",
    "TARGET_UNIX",
    "TARGET_LINUX",
    # clrdefinitions.cmake
    "FEATURE_CORECLR",
    "FEATURE_JIT",
    "UNIX_AMD64_ABI",
    "DEBUGGING_SUPPORTED",
    "PROFILING_SUPPORTED",
    "FEATURE_METADATA_UPDATER",
    "FEATURE_REMAP_FUNCTION",
    "FEATURE_COLLECTIBLE_TYPES",
    "FEATURE_BASICFREEZE",
    "FEATURE_DBGIPC_TRANSPORT_DI",
    "FEATURE_DBGIPC_TRANSPORT_VM",
    "FEATURE_DEFAULT_INTERFACES",
    "FEATURE_EVENT_TRACE=1",
    "FEATURE_PERFTRACING=1",
    "FEATURE_GDBJIT",
    "FEATURE_GDBJIT_FRAME",
    "FEATURE_GDBJIT_LANGID_CS",
    "FEATURE_GDBJIT_SYMTAB",
    "FEATURE_EVENTSOURCE_XPLAT=1",
    "FEATURE_HIJACK",
    "FEATURE_PERFMAP",
    "FEATURE_PAL_ANSI",
    "FEATURE_MULTICOREJIT",
    "FEATURE_READYTORUN",
    "FEATURE_REMOTE_PROC_MEM",
    "FEATURE_SVR_GC",
    "FEATURE_SYMDIFF",
    "FEATURE_CODE_VERSIONING",
    "FEATURE_TIERED_COMPILATION=1",
    "FEATURE_PGO",
    "UNIX_AMD64_ABI_ITF",
    "FEATURE_USE_ASM_GC_WRITE_BARRIERS",
    "FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP",
    "FEATURE_MANUALLY_MANAGED_CARD_BUNDLES",
    "_SECURE_SCL=0",
    "UNICODE",
    "_UNICODE",
    # clrfeatures.cmake
    "FEATURE_REJIT=1",
    "FEATURE_DBGIPC=1",
    "FEATURE_INTERPRETER=0",
    "FEATURE_STANDALONE_GC=1",
    "FEATURE_AUTO_TRACE=0",
    "FEATURE_SINGLE_FILE_DIAGNOSTICS=1",
    "FEATURE_COMWRAPPERS=1",
    "FEATURE_JAVAMARSHAL=0",
    "FEATURE_CORECLR_CACHED_INTERFACE_DISPATCH=0",
    "FEATURE_CORECLR_VIRTUAL_STUB_DISPATCH=1",
    "FEATURE_CORECLR_FLUSH_INSTRUCTION_CACHE_TO_PROTECT_STUB_READS=1",
]

# --- Global include paths for all coreclr components ---
# Matches include_directories() from src/coreclr/CMakeLists.txt (Unix path).

CORECLR_COPTS = [
    # Warning suppression matching configurecompiler.cmake + src/coreclr/CMakeLists.txt for GCC C++
    "-Wno-invalid-offsetof",
    "-Wno-class-memaccess",
    "-Wno-conversion-null",
    "-Wno-pointer-arith",
    "-Wno-misleading-indentation",
    "-Wno-stringop-overflow",
    "-Wno-restrict",
    # Include paths (matching include_directories from src/coreclr/CMakeLists.txt)
    "-Isrc/coreclr/inc",
    "-Isrc/coreclr/pal/inc",
    "-Isrc/coreclr/pal/inc/rt",
    "-Isrc/coreclr/pal/src/safecrt",
    "-Isrc/coreclr/pal/prebuilt/inc",
    "-Isrc/coreclr/debug/inc",
    "-Isrc/coreclr/debug/inc/amd64",
    "-Isrc/coreclr/debug/inc/dump",
    "-Isrc/coreclr/md/inc",
    "-Isrc/coreclr/hosts/inc",
    "-Isrc/coreclr/interpreter/inc",
    "-Isrc/coreclr/minipal",
    "-Isrc/native",
    "-Isrc/native/inc",
]
