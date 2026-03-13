# Platform defines matching CMake configurecompiler.cmake.
# Used by native cc_library targets via local_defines = PLATFORM_DEFINES.
#
# This file is intentionally kept free of @rules_dotnet loads so that
# changes to the C# tooling / rules_dotnet version don't invalidate the
# Bazel analysis cache for C++ targets.
PLATFORM_DEFINES = [
    "DISABLE_CONTRACTS",
] + select({
    "@platforms//os:macos": [
        "HOST_64BIT",
        "HOST_ARM64",
        "HOST_UNIX",
        "HOST_APPLE",
        "HOST_OSX",
        "TARGET_64BIT",
        "TARGET_ARM64",
        "TARGET_UNIX",
        "TARGET_APPLE",
        "TARGET_OSX",
        # configurecompiler.cmake — macOS platform defines
        "_XOPEN_SOURCE",
        "_DARWIN_C_SOURCE",
        "__DARWIN_NON_CANCELABLE=1",
        # src/native/libs/CMakeLists.txt — macOS networking
        "__APPLE_USE_RFC_3542",
    ],
    "@platforms//os:windows": [
        "HOST_64BIT",
        "HOST_AMD64",
        "HOST_WINDOWS",
        "TARGET_64BIT",
        "TARGET_AMD64",
        "TARGET_WINDOWS",
        # configurecompiler.cmake — Windows platform defines
        "WIN32",
        "_WIN32",
        "_WIN64",
        "UNICODE",
        "_UNICODE",
        "_CRT_SECURE_NO_WARNINGS",
        "_CRT_NONSTDC_NO_WARNINGS",
    ],
    "@platforms//os:linux": [
        "_GNU_SOURCE",
        "HOST_64BIT",
        "HOST_AMD64",
        "HOST_UNIX",
        "TARGET_64BIT",
        "TARGET_AMD64",
        "TARGET_UNIX",
        "TARGET_LINUX",
    ],
})

# Platform-specific compiler flags matching CMake configurecompiler.cmake.
# Used by native cc_library targets via copts = PLATFORM_COPTS.
#
# Unix flags use GCC/Clang syntax; Windows flags use MSVC syntax.
# Platform selection is handled via select() so the correct flags are
# applied automatically on each platform.
#
# Language-standard and C++-only flags live in PLATFORM_CONLYOPTS and
# PLATFORM_CXXOPTS respectively (Bazel 7.4.0+ added conlyopts/cxxopts
# attributes on cc_* rules).
#
# per_file_copt has no BUILD-file equivalent and remains in .bazelrc.

# --- Unix (GCC/Clang) flags from configurecompiler.cmake ---

_UNIX_COPTS = [
    # Code generation
    "-fPIC",
    "-fno-omit-frame-pointer",
    "-fno-strict-overflow",
    "-fno-strict-aliasing",
    "-fstack-protector-strong",
    "-ffp-contract=off",
    "-fsigned-char",
    "-fvisibility=hidden",
    "-ffunction-sections",
    # Universal platform defines
    "-D_FILE_OFFSET_BITS=64",
    "-D_TIME_BITS=64",
    # Warning flags shared between GCC and Clang (configurecompiler.cmake)
    "-Wall",
    "-Wno-unused-variable",
    "-Wno-unused-value",
    "-Wno-unused-function",
    "-Wno-tautological-compare",
    "-Wno-unknown-pragmas",
    "-Wimplicit-fallthrough",
    "-Wno-unused-but-set-variable",
    # Bazel's default C++ toolchain enables -Wunused-but-set-parameter; CMake does not.
    "-Wno-unused-but-set-parameter",
    # GCC-specific warning flags (silently ignored by Clang with
    # -Wno-unknown-warning-option).
    "-Wno-uninitialized",
    "-Wno-strict-aliasing",
    "-Wno-array-bounds",
    "-Wno-stringop-truncation",
    # Clang-specific flags (silently ignored by GCC).
    "-Wno-unknown-warning-option",
    "-ferror-limit=4096",
    "-Wno-null-conversion",
    "-Wno-unused-private-field",
    "-Wno-constant-logical-operand",
    "-Wno-pragma-pack",
    "-Wno-incompatible-ms-struct",
    "-Wno-reserved-identifier",
    "-Wno-unsafe-buffer-usage",
    "-Wno-single-bit-bitfield-constant-conversion",
    "-Wno-cast-function-type-strict",
    "-Wno-switch-default",
]

_UNIX_CONLYOPTS = [
    "-std=gnu11",
]

_UNIX_CXXOPTS = [
    "-std=gnu++11",
    "-fno-rtti",
    # GCC C++-specific warnings (silently ignored by Clang).
    "-Wno-misleading-indentation",
    "-Wno-stringop-overflow",
    "-Wno-restrict",
    "-Wno-class-memaccess",
    # Clang C++-specific warnings.
    "-Wno-nontrivial-memaccess",
]

PLATFORM_COPTS = select({
    "@platforms//os:windows": [
        # Exception handling & code generation
        "/EHsc",
        "/GS",
        "/Oi",
        "/Oy-",
        "/Gm-",
        "/Gy",
        "/fp:precise",
        "/GR-",
        "/FC",
        "/Zp8",
        # Security
        "/guard:cf",
        # Conformance
        "/Zc:strictStrings",
        "/Zc:wchar_t",
        "/Zc:inline",
        "/Zc:forScope",
        "/source-charset:utf-8",
        # Warnings
        "/W4",
        "/wd4005",
        "/wd4100",
        "/wd4127",
        "/wd4131",
        "/wd4189",
        "/wd4200",
        "/wd4201",
        "/wd4206",
        "/wd4239",
        "/wd4245",
        "/wd4291",
        "/wd4310",
        "/wd4324",
        "/wd4366",
        "/wd4456",
        "/wd4457",
        "/wd4458",
        "/wd4459",
        "/wd4463",
        "/wd4505",
        "/wd4702",
        "/wd4706",
        "/wd4733",
        "/wd4815",
        "/wd4838",
        "/wd4918",
        "/wd4960",
        "/wd4961",
        "/wd5105",
        "/wd5205",
    ],
    "//conditions:default": _UNIX_COPTS,
})

# C-only flags (language standard).  Pass via conlyopts = PLATFORM_CONLYOPTS.
PLATFORM_CONLYOPTS = select({
    "@platforms//os:windows": [],
    "//conditions:default": _UNIX_CONLYOPTS,
})

# C++-only flags (language standard, RTTI, C++ warnings).
# Pass via cxxopts = PLATFORM_CXXOPTS.
PLATFORM_CXXOPTS = select({
    "@platforms//os:windows": [],
    "//conditions:default": _UNIX_CXXOPTS,
})

# Platform-specific linker flags matching CMake configurecompiler.cmake.
# Used by native cc_library / cc_binary / cc_shared_library targets
# via linkopts or user_link_flags.
PLATFORM_LINKOPTS = select({
    "@platforms//os:windows": [
        "/MANIFEST:NO",
        "/LARGEADDRESSAWARE",
        "/DEBUGTYPE:CV,FIXUP",
        "/PDBCOMPRESS",
        "/DEPENDENTLOADFLAG:0x800",
        "/STACK:0x180000",
        "/guard:cf",
    ],
    "//conditions:default": [],
})
