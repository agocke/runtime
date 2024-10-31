load("//:defs.bzl", "csharp_library", "NETCOREAPP_CURRENT")
load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@rules_dotnet//dotnet/private:providers.bzl", "DotnetAssemblyCompileInfo")
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("@rules_dotnet//dotnet/private/rules/csharp:binary.bzl", "compile_csharp_exe")
load("@rules_dotnet//dotnet/private:common.bzl", "is_debug",)
load("//src/libraries:defs.bzl", "live_csharp_library", "LIVE_REFPACK_DEPS")
load("//src/tests:defs.bzl", "COMMON_ATTRS", "build_binary")

def _live_csharp_test_impl(ctx):
    result = build_binary(ctx, compile_csharp_exe)
    return result

def _to_dict(s):
    return {
        key: getattr(s, key) for key in dir(s)
        if key != "to_json" and key != "to_proto" and key != "aspect_ids"
    }


_live_csharp_test = rule(
    _live_csharp_test_impl,
    doc = """Compile a C# exe for the live framework""",
    attrs = dicts.add(
        COMMON_ATTRS,
        {
            "_launcher_sh": attr.label(
                doc = "A template file for the launcher on Linux/MacOS",
                default = "//:eng/run_test.sh.tpl",
                allow_single_file = True,
            ),
            "aliased_deps": attr.label_keyed_string_dict(
                doc = "The dependencies to transform",
                default = {},
            ),
            "_live_refpack_deps": attr.label_list(
                doc = "The refpack dependencies for the live framework",
                default = LIVE_REFPACK_DEPS,
            ),
        }),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def coreclr_test_library(
    name,
    deps = [],
    **kwargs
):
    deps = deps + [
        "@paket.main//microsoft.dotnet.xunitassert",
        "@paket.main//xunit.abstractions",
        "@paket.main//xunit.extensibility.core",
    ]
    live_csharp_library(
        name = name,
        deps = deps,
        nowarn = [
            "CS3001",
            "CS3002",
            "CS3003",
        ],
        visibility = ["//visibility:public"],
        **kwargs
    )

def _transform_deps_impl(ctx):
    # Transform all explicit deps into deps with extern alias
    for dep in ctx.attr.deps:
        compile = dep[DotnetAssemblyCompileInfo]
        print(compile)
        compile.alias = compile.name
    return ctx.attr.deps

_transform_deps = rule(
    _transform_deps_impl,
    attrs = {
        "deps": attr.label_list(
            doc = "The dependencies to transform",
            providers = [DotnetAssemblyCompileInfo],
        ),
    },
)

def coreclr_merged_test(
    name,
    deps = [],
    **kwargs
):
    print (deps)
    _transform_deps(
        name = "_transform_deps_" + name,
        deps = deps,
    )

    _live_csharp_test(
        name = name,
        target_frameworks = ["net9.0"],
        deps = [ ":_transform_deps_" + name ],
        **kwargs
    )