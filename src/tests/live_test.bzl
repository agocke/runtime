load("//:defs.bzl", "csharp_library", "NETCOREAPP_CURRENT")
load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@bazel_skylib//rules:common_settings.bzl", "BuildSettingInfo")
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("@rules_dotnet//dotnet/private/rules/csharp/actions:csharp_assembly.bzl", "AssemblyAction")
load("@rules_dotnet//dotnet/private:common.bzl", "is_debug",)
load("//src/libraries:defs.bzl", "live_csharp_library", "LIVE_REFPACK_DEPS")
load("//src/tests:defs.bzl", "COMMON_ATTRS", "build_binary")

def _compile_action(ctx, tfm):
    toolchain = ctx.toolchains["@rules_dotnet//dotnet:toolchain_type"]
    return AssemblyAction(
        ctx.actions,
        ctx.executable._compiler_wrapper_bat if ctx.target_platform_has_constraint(ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]) else ctx.executable._compiler_wrapper_sh,
        additionalfiles = ctx.files.additionalfiles,
        direct_analyzers = ctx.attr.analyzers,
        debug = is_debug(ctx),
        defines = ctx.attr.defines,
        deps = ctx.attr.deps,
        exports = [],
        targeting_pack = ctx.attr._targeting_pack[0],
        internals_visible_to = ctx.attr.internals_visible_to,
        cls_compliant = ctx.attr.cls_compliant,
        assembly_version = ctx.attr.assembly_version,
        keyfile = ctx.file.keyfile,
        langversion = ctx.attr.langversion,
        resources = ctx.files.resources,
        srcs = ctx.files.srcs,
        data = ctx.files.data,
        appsetting_files = ctx.files.appsetting_files,
        compile_data = ctx.files.compile_data,
        out = ctx.attr.out,
        target = "exe",
        target_name = ctx.attr.name,
        target_framework = tfm,
        toolchain = toolchain,
        strict_deps = toolchain.strict_deps[BuildSettingInfo].value,
        generate_documentation_file = ctx.attr.generate_documentation_file,
        include_host_model_dll = ctx.attr.include_host_model_dll,
        treat_warnings_as_errors = ctx.attr.treat_warnings_as_errors,
        warnings_as_errors = ctx.attr.warnings_as_errors,
        warnings_not_as_errors = ctx.attr.warnings_not_as_errors,
        warning_level = ctx.attr.warning_level,
        nowarn = ctx.attr.nowarn,
        project_sdk = ctx.attr.project_sdk,
        allow_unsafe_blocks = ctx.attr.allow_unsafe_blocks,
        nullable = ctx.attr.nullable,
        run_analyzers = ctx.attr.run_analyzers,
        compiler_options = ctx.attr.compiler_options,
        ref_assembly = False,
        is_windows = ctx.target_platform_has_constraint(ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]),
    )

def _live_csharp_test_impl(ctx):
    result = build_binary(ctx, _compile_action)
    return result

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