load(
    "@rules_dotnet//dotnet:defs.bzl",
    "csharp_library"
)

load(
    "//:defs.bzl",
    "NETCOREAPP_CURRENT"
)
load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyRuntimeInfo",
)

load("@bazel_skylib//rules:run_binary.bzl", "run_binary")

# Convenience macro for defining a ref assembly for the NetCoreApp framework.
def netcoreapp_ref_assembly(
    name,
    srcs,
    deps = [],
    nowarn = [],
    compiler_options = [],
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
        keyfile = "@@_main~main_extension~nuget.microsoft.dotnet.arcade.sdk.v9.0.0-beta.24423.2//:tools/snk/MSFT.snk",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        nowarn = nowarn,
        compiler_options = compiler_options,
        **kwargs
    )

def _gen_facades_impl(ctx):
    libs_paths = [r[DotnetAssemblyRuntimeInfo].libs[0] for r in ctx.attr.ref_paths]
    contract_assembly = ctx.attr.facade_contract_assembly[DotnetAssemblyRuntimeInfo].libs[0]
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = libs_paths + [contract_assembly],
        outputs = [ctx.outputs.out],
        arguments = [
            "--outputSourcePath=%s" % ctx.outputs.out.path,
            "--contractAssembly=%s" % contract_assembly.path,
        ]
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

def netcoreapp_impl_library(
    name,
    srcs,
    deps = [],
    compiler_options = [],
    generate_facades = False,
    facade_contract_assembly = None,
    facade_omit_types = [],
    **kwargs
):
    base_name = name[len("impl_"):]

    compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/publicsign+",
    ]

    if generate_facades:
        forwards_cs = name + ".Forwards.cs"
        gen_facades(
            name = "facade_" + base_name,
            srcs = srcs,
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
        deps = deps,
        assembly_version = "9.0.0.0",
        visibility = [ "//visibility:public" ],
        nullable = "annotations",
        keyfile = "@@_main~main_extension~nuget.microsoft.dotnet.arcade.sdk.v9.0.0-beta.24423.2//:tools/snk/MSFT.snk",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        compiler_options = compiler_options,
        **kwargs
    )