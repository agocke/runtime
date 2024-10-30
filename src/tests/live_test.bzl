load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@rules_dotnet//dotnet:defs.bzl", "compile_csharp_exe")
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("//src/libraries:defs.bzl", "LIVE_REFPACK_DEPS")
load("//src/tests:defs.bzl", "COMMON_ATTRS", "build_binary")

def _live_csharp_test_impl(ctx):
    result = build_binary(ctx, compile_csharp_exe)
    return result

_live_csharp_test = rule(
    _live_csharp_test_impl,
    doc = """Compile a C# exe for the live framework""",
    attrs = dicts.add(COMMON_ATTRS, {
        "_launcher_sh": attr.label(
            doc = "A template file for the launcher on Linux/MacOS",
            default = "//:eng/run_test.sh.tpl",
            allow_single_file = True,
        )}),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def coreclr_merged_test(
    name,
    deps = [],
    **kwargs
):
    deps = deps + LIVE_REFPACK_DEPS
    _live_csharp_test(
        name = name,
        target_frameworks = ["net9.0"],
        deps = deps,
        **kwargs
    )