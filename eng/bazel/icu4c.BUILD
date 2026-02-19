# External BUILD file for ICU4C (homebrew on macOS, system on Linux).
# Provides unicode/ headers for System.Globalization.Native.

load("@rules_cc//cc:defs.bzl", "cc_library")

cc_library(
    name = "headers",
    hdrs = glob(["include/unicode/*.h"]),
    includes = ["include"],
    visibility = ["//visibility:public"],
)
