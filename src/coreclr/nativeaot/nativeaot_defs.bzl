# Shared constants for NativeAOT Bazel builds (linux-x64).
# Derived from src/coreclr/nativeaot/CMakeLists.txt and
# src/coreclr/nativeaot/Runtime/CMakeLists.txt.

NATIVEAOT_DEFINES = [
    # Platform
    "HOST_64BIT",
    "HOST_AMD64",
    "HOST_UNIX",
    "TARGET_64BIT",
    "TARGET_AMD64",
    "TARGET_UNIX",
    "TARGET_LINUX",
    # NativeAOT core
    "FEATURE_NATIVEAOT",
    "NATIVEAOT",
    "VERIFY_HEAP",
    "FEATURE_BASICFREEZE",
    "FEATURE_CONSERVATIVE_GC",
    "FEATURE_CACHED_INTERFACE_DISPATCH",
    "_LIB",
    # AMD64 features
    "FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP",
    "FEATURE_MANUALLY_MANAGED_CARD_BUNDLES",
    # Unix (non-Apple, non-WASM)
    "FEATURE_READONLY_GS_COOKIE",
    # ABI
    "UNIX_AMD64_ABI",
    # Diagnostics
    "FEATURE_HIJACK",
    "FEATURE_PERFTRACING",
    "FEATURE_EVENT_TRACE=1",
    # Full runtime (non-Apple)
    "FEATURE_RX_THUNKS",
    # libunwind integration
    "_LIBUNWIND_DISABLE_ZERO_COST_APIS=1",
    "_LIBUNWIND_IS_NATIVE_ONLY",
]

NATIVEAOT_COPTS = [
    # From nativeaot/CMakeLists.txt — no C++ exceptions, no async unwind tables
    "-fno-exceptions",
    "-fno-asynchronous-unwind-tables",
    # AMD64: allow 16-byte compare-exchange
    "-mcx16",
    # Warning suppression (matching configurecompiler.cmake)
    "-Wno-invalid-offsetof",
    "-Wno-class-memaccess",
    "-Wno-conversion-null",
    "-Wno-pointer-arith",
    "-Wno-misleading-indentation",
    "-Wno-stringop-overflow",
    "-Wno-restrict",
    "-Wno-unused-but-set-parameter",
    # Include paths (matching include_directories from Runtime/CMakeLists.txt)
    "-Isrc/coreclr/nativeaot/Runtime",
    "-Isrc/coreclr/nativeaot/Runtime/inc",
    "-Isrc/coreclr/nativeaot/Runtime/unix",
    "-Isrc/coreclr/nativeaot/Runtime/amd64",
    "-Isrc/coreclr/gc",
    "-Isrc/coreclr/gc/env",
    "-Isrc/coreclr/runtime",
    "-Isrc/coreclr/pal/inc/rt",
    "-Isrc/native",
    "-Isrc/native/inc",
    "-Isrc/native/external/llvm-libunwind/include",
]
