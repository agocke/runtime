load("//:defs.bzl", "NETCOREAPP_CURRENT")
load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",)
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("@rules_dotnet//dotnet/private/rules/csharp:binary.bzl", "compile_csharp_exe")
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
        }),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def live_csharp_test(
    name,
    deps = [],
    analyzers = [],
    nowarn = [],
    **kwargs
):
    analyzers = analyzers + [
        "//src/tests/Common:XUnitWrapperGenerator",
    ]
    deps = deps + LIVE_REFPACK_DEPS
    _live_csharp_test(
        name = name,
        deps = deps,
        analyzers = analyzers,
        target_frameworks = [NETCOREAPP_CURRENT],
        nowarn = nowarn + [ "CS1701" ],
        **kwargs
    )

def coreclr_test(
    name,
    deps = [],
    size = "small",
    pri = 0,
    tags = [],
    debug_type = "portable", # TODO: plum through to compiler
    optimize = False, # TODO: plum through to compiler
    compiler_options = [],
    **kwargs
):
    deps = deps + [
        "@paket.main//microsoft.dotnet.xunitassert",
        "@paket.main//xunit.abstractions",
        "@paket.main//xunit.extensibility.core",
    ]

    compiler_options = [
        "/debug:%s" % debug_type,
        "/optimize%s" % ("" if optimize else "-"),
    ] + compiler_options

    # Create two targets: a library for the merged runner and a test. We'll use one or the other.
    live_csharp_library(
        name = name + "_lib",
        deps = deps,
        nowarn = [
            "CS3001",
            "CS3002",
            "CS3003",
        ],
        tags = tags,
        visibility = ["//visibility:public"],
        compiler_options = compiler_options,
        **kwargs
    )

    live_csharp_test(
        name = name,
        deps = deps,
        size = size,
        tags = tags + [ "pri%d" % pri ],
        compiler_options = compiler_options,
        **kwargs
    )

def _transform_dep_impl(ctx):
    # Transform explicit dep into dep with extern alias
    dep = ctx.attr.dep
    compile = dep[DotnetAssemblyCompileInfo]
    compile_dict = _to_dict(compile)
    compile_dict.pop("alias")
    newcomp = DotnetAssemblyCompileInfo(
        alias = "_" + compile.name.replace(".", "_"),
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

def _il_test_impl(ctx):
    args = []
    if ctx.attr.debug_type == "full":
        args.append("-debug")
    if ctx.attr.debug_type == "pdbonly":
        args.append("-debug=opt")
    if ctx.attr.optimize:
        args.append("-optimize")

    ctx.actions.run(
        inputs = ctx.files.srcs,
        outputs = [ctx.outputs.out],
        arguments = args,
        progress_message = "Compiling %s" % ctx.outputs.out.short_path,
        executable = ctx.executable.ilasm_exe,
    )

    ctx.actions.expand_template(
        template = ctx.file._launcher_sh,
        output = launcher,
        substitutions = {
            "TEMPLATED_dotnet": to_rlocation_path(ctx, runtime.files_to_run.executable),
            "TEMPLATED_executable": to_rlocation_path(ctx, executable),
        },
        is_executable = True,
    )
    runfiles.append(ctx.file._bash_runfiles)

_il_test = rule(
    implementation = _il_test_impl,
    attrs = {
        "srcs": attr.label_list(
            doc = "The source files to compile",
            allow_files = True,
        ),
        "out": attr.output(
            mandatory = True,
            doc = "The output DLL.",
        ),
        "debug_type": attr.string(
            doc = "The debug type",
            default = "full",
        ),
        "optimize": attr.bool(
            doc = "Enable optimization.",
            default = False,
        ),
        "ilasm_exe": attr.label(
            default = Label("//:artifacts/bin/coreclr/linux.x64.Debug/ilasm"),
            cfg = "exec",
            executable = True,
            allow_files = True,
        ),
        "_launcher_sh": attr.label(
            doc = "A template file for the launcher on Linux/MacOS",
            default = "//:eng/run_test.sh.tpl",
            allow_single_file = True,
        ),
    },
    test = True,
)

def il_coreclr_test(
    name,
    srcs,
    **kwargs
):
    _il_test(
        name = name,
        srcs = srcs,
        out = name + ".dll",
        **kwargs
    )


def coreclr_merged_test(
    name,
    deps = [],
    test_deps = [],
    tags = [],
    **kwargs
):
    """ Create a merged test that includes all of the test_deps as test sources.

    Args:
        name: The name of the test
        deps: The dependencies of the test
        test_deps: The test dependencies to merge
        tags: The tags for the test
        **kwargs: Additional arguments to pass to live_csharp_test
    """

    # Tests may have the same types, so we need to add extern aliases
    transformed_deps = []
    for (i, dep) in enumerate(test_deps):
        transform_label_name = "_transform_dep_%s_%s" % (name, i)
        # coreclr_test creates two targets, one library and one test. We need the library target as
        # a dependency.
        dep_label = native.package_relative_label(dep)
        lib_dep =  dep_label.same_package_label(dep_label.name + "_lib")

        _transform_dep(
            name = transform_label_name,
            dep = lib_dep,
        )
        transformed_deps.append(":" + transform_label_name)

    live_csharp_test(
        name = name,
        deps = deps + transformed_deps,
        tags = tags + ["merged", "manual"],
        **kwargs
    )