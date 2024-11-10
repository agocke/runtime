
load(
    "//:defs.bzl",
    "NETCOREAPP_CURRENT",
    "csharp_library",
)

load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",
    "DotnetTargetingPackInfo",
)

load("@bazel_skylib//rules:run_binary.bzl", "run_binary")

LIVE_NETCOREAPP_DEPS = [
#    "//src/libraries:live_System.Runtime",
#    "//src/libraries:live_System.Console",
#    "//src/libraries/System.Runtime.InteropServices:live_System.Runtime.InteropServices",
#    "//src/libraries:live_Microsoft.Win32.Primitives",
#    "//src/libraries:live_System.Collections",
]

LIVE_REFPACK_DEPS = [
    # Roughly topologically sorted
    "//src/libraries:ref_System.Runtime",
    "//src/libraries/System.Runtime.Loader:ref_System.Runtime.Loader",
    "//src/libraries/System.Console:ref_System.Console",
    "//src/libraries/System.Collections:ref_System.Collections",
    "//src/libraries/System.Linq:ref_System.Linq",
    "//src/libraries/System.Collections.NonGeneric:ref_System.Collections.NonGeneric",
    "//src/libraries/System.ComponentModel:ref_System.ComponentModel",
    "//src/libraries:ref_System.Diagnostics.FileVersionInfo",
    "//src/libraries:ref_System.Diagnostics.Process",
    "//src/libraries/System.Memory:ref_System.Memory",
    "//src/libraries/System.Runtime.Intrinsics:ref_System.Runtime.Intrinsics",
    "//src/libraries/System.Numerics.Vectors:ref_System.Numerics.Vectors",
    "//src/libraries/System.ObjectModel:ref_System.ObjectModel",
    "//src/libraries:ref_System.ComponentModel.Primitives",
    "//src/libraries:ref_System.Collections.Specialized",
    "//src/libraries/System.Runtime.InteropServices:ref_System.Runtime.InteropServices",
]

# Convenience macro for defining a ref assembly for the NetCoreApp framework.
def netcoreapp_ref_assembly(
    name,
    srcs,
    deps = [],
    nowarn = [],
    compiler_options = [],
    keyfile = None,
    **kwargs
):
    compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/publicsign+",
    ]
    nowarn = nowarn + [
        "CS0169",
    ]
    base_name = name[len("ref_"):]
    csharp_library(
        name = name,
        out = base_name,
        srcs = srcs,
        deps = deps,
        assembly_version = "9.0.0.0",
        visibility = [ "//visibility:public" ],
        nullable = "annotations",
        keyfile = keyfile if keyfile else "@@_main~main_extension~nuget.microsoft.dotnet.arcade.sdk.v9.0.0-beta.24423.2//:tools/snk/MSFT.snk",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        nowarn = nowarn,
        compiler_options = compiler_options,
        ref_assembly = True,
        **kwargs
    )

def _gen_facades_impl(ctx):
    libs_paths = [r[DotnetAssemblyCompileInfo].irefs[0] for r in ctx.attr.ref_paths]
    contract_assembly = ctx.attr.facade_contract_assembly[DotnetAssemblyCompileInfo].irefs[0]

    args = [
        "--outputSourcePath=%s" % ctx.outputs.out.path,
        "--contractAssembly=%s" % contract_assembly.path,
    ]

    if len(ctx.attr.defines) > 0:
        args.append("--defines=%s" % ";".join(ctx.attr.defines))

    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = ctx.files.srcs + libs_paths + [contract_assembly],
        outputs = [ctx.outputs.out],
        arguments = args
            + [ "--src=%s" % s.path for s in ctx.files.srcs ]
            + [ "--omitType=%s" % t for t in ctx.attr.facade_omit_types ]
            + ["--ref-path=%s" % p.path for p in libs_paths],
    )

gen_facades = rule(
    implementation = _gen_facades_impl,
    attrs = {
        "srcs": attr.label_list(
            allow_files = True,
            doc = "The source files to generate facades for.",
        ),
        "defines": attr.string_list(
            doc = "The defines to use when generating facades.",
        ),
        "out": attr.output(mandatory = True),
        "ref_paths": attr.label_list(
            doc = "The paths to the reference assemblies.",
        ),
        "facade_contract_assembly": attr.label(
            doc = "The contract assembly to use.",
        ),
        "facade_omit_types": attr.string_list(
            doc = "The types to omit from the generated facades.",
        ),
        "_exe": attr.label(
            default = Label("//src/tools/GenFacades:GenFacades"),
            cfg = "exec",
            executable = True,
        ),
    }
)

def _gen_resx_source_impl(ctx):
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = [ctx.file.resx_file],
        outputs = [ctx.outputs.out],
        arguments = [
            "--output-path=%s" % ctx.outputs.out.path,
            "--resource-name=%s" % ("FxResources." + ctx.attr.assembly_name + ".SR"),
            "--resource-file=%s" % ctx.file.resx_file.path,
        ],
    )

gen_resx_source = rule(
    implementation = _gen_resx_source_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "assembly_name": attr.string(mandatory = True),
        "resx_file": attr.label(
            mandatory = True,
            allow_single_file = True,
        ),
        "_exe": attr.label(
            default = Label("//src/tools/GenerateResxSource:GenerateResxSource"),
            cfg = "exec",
            executable = True,
        ),
    }
)

def netcoreapp_impl_assembly(
    name,
    srcs = [],
    deps = [],
    defines = [],
    compiler_options = [],
    generate_facades = False,
    facade_contract_assembly = None,
    facade_omit_types = [],
    resx_file = None,
    exclude_sr = False,
    keyfile = None,
    **kwargs
):
    base_name = name[len("impl_"):]

    if not exclude_sr:
        srcs = srcs + [
            "//src/libraries/Common:src/System/SR.cs",
        ]
    compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/publicsign+",
    ]

    if generate_facades:
        forwards_cs = name + ".Forwards.cs"
        gen_facades(
            name = "facade_" + base_name,
            srcs = srcs,
            defines = defines,
            out = forwards_cs,
            ref_paths = deps,
            facade_contract_assembly = facade_contract_assembly,
            facade_omit_types = facade_omit_types,
        )
        srcs = srcs + [ ":facade_" + base_name ]

    csharp_library(
        name = name,
        out = base_name,
        srcs = srcs,
        defines = defines,
        deps = deps,
        assembly_version = "9.0.0.0",
        visibility = [ "//visibility:public" ],
        nullable = "annotations",
        keyfile = keyfile if keyfile else "@@_main~main_extension~nuget.microsoft.dotnet.arcade.sdk.v9.0.0-beta.24423.2//:tools/snk/MSFT.snk",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        compiler_options = compiler_options,
        resx_file = resx_file,
        **kwargs
    )

def _ref_impl_pair(ctx):
    return [
        ctx.attr.ref[DotnetAssemblyCompileInfo],
        ctx.attr.lib[DotnetAssemblyRuntimeInfo],
    ]

ref_impl_pair = rule(
    implementation = _ref_impl_pair,
    attrs = {
        "ref": attr.label(
            doc = "The reference assembly to use.",
            providers = [DotnetAssemblyCompileInfo],
        ),
        "lib": attr.label(
            doc = "The libraries to use.",
            providers = [DotnetAssemblyRuntimeInfo],
        ),
    }
)

def live_csharp_library(
    name,
    deps = [],
    **kwargs
):
    deps = deps + LIVE_REFPACK_DEPS

    csharp_library(
        name = name,
        deps = deps,
        disable_implicit_framework_refs = True,
        target_frameworks = [ NETCOREAPP_CURRENT ],
        **kwargs
    )