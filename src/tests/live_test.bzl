load("//:defs.bzl", "csharp_library", "NETCOREAPP_CURRENT")
load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",
    "NuGetInfo",)
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("@rules_dotnet//dotnet/private/rules/csharp:binary.bzl", "compile_csharp_exe")
load("@rules_dotnet//dotnet/private:common.bzl", "is_debug",)
load("//src/libraries:defs.bzl", "live_csharp_library", "LIVE_REFPACK_DEPS")
load("//src/tests:defs.bzl", "COMMON_ATTRS", "build_binary")

def _live_csharp_test_impl(ctx):
    print(ctx.attr.deps)
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

def _transform_dep_impl(ctx):
    # Transform explicit dep into dep with extern alias
    dep = ctx.attr.dep
    compile = dep[DotnetAssemblyCompileInfo]
    compile_dict = _to_dict(compile)
    compile_dict.pop("alias")
    newcomp = DotnetAssemblyCompileInfo(
        alias = "_" + compile.name,
        **compile_dict
    )
    default_info = dep[DefaultInfo]
    runtime_info = dep[DotnetAssemblyRuntimeInfo]
    return [
        default_info,
        newcomp,
        runtime_info,
    ]

_transform_dep = rule(
    _transform_dep_impl,
    attrs = {
        "dep": attr.label(
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
    transformed_deps = []
    for (i, dep) in enumerate(deps):
        label_name = "_transform_dep_%s_%s" % (name, i)
        _transform_dep(
            name = label_name,
            dep = dep,
        )
        transformed_deps.append(":" + label_name)

    _live_csharp_test(
        name = name,
        target_frameworks = ["net9.0"],
        deps = transformed_deps + LIVE_REFPACK_DEPS,
        **kwargs
    )