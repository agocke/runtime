# Shared native library definitions for debug/release configuration.
#
# Usage in BUILD files:
#   load("//src/native:native_defs.bzl", "NATIVE_CONFIG_DEFINES")
#   cc_library(
#       ...
#       local_defines = NATIVE_CONFIG_DEFINES,
#   )

NATIVE_CONFIG_DEFINES = select({
    "//:libs_debug": [
        "DEBUG",
        "_DEBUG",
    ],
    "//:libs_release": [
        "NDEBUG",
    ],
})
