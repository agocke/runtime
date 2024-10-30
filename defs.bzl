load("@rules_dotnet//dotnet:defs.bzl", _base_csharp_library="csharp_library")

# The TFM that we're building
NETCOREAPP_CURRENT = "net9.0"
# The TFM used by our LKG SDK
NETCOREAPP_TOOL_CURRENT = "net9.0"

def from_coreclr_artifacts(file):
    return select({
        "@platforms//os:linux": [ Label("//:artifacts/bin/coreclr/linux.x64.Debug/%s" % file) ],
        "@platforms//os:macos": [ Label("//:artifacts/bin/coreclr/osx.arm64.Debug/%s" % file) ],
    })

def _gen_resx_source_impl(ctx):
    resource_name = "FxResources.%s.SR" % ctx.attr.assembly_name
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = [ctx.file.resx_file],
        outputs = [ctx.outputs.out],
        arguments = [
            "--output-path=%s" % ctx.outputs.out.path,
            "--resource-name=%s" % resource_name,
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

def _resgen_impl(ctx):
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = [ctx.file.resx_file],
        outputs = [ctx.outputs.out],
        arguments = [
            "--src-path=%s" % ctx.file.resx_file.path,
            "--out-path=%s" % ctx.outputs.out.path,
        ],
    )

resgen = rule(
    implementation = _resgen_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "resx_file": attr.label(
            mandatory = True,
            allow_single_file = True,
        ),
        "_exe": attr.label(
            default = Label("//src/tools/ResGen:ResGen"),
            cfg = "exec",
            executable = True,
        ),
    }
)

def csharp_library(
    name,
    srcs = [],
    out = None,
    resx_file = None,
    resources = [],
    **kwargs
):
    if out == None:
        out = name

    if resx_file != None:
        resgen_target = "resgen_" + name
        resgen_out = "FxResources.%s.SR.resources" % out
        resgen(
            name = resgen_target,
            out = resgen_out,
            resx_file = resx_file,
        )
        resources = resources + [ ":" + resgen_target ]

        resx_target = "resx_" + name
        gen_resx_source(
            name = resx_target,
            out = out + ".System.SR.cs",
            assembly_name = out,
            resx_file = resx_file,
        )
        srcs = srcs + [ ":" + resx_target ]

    _base_csharp_library(
        name = name,
        srcs = srcs,
        out = out,
        resources = resources,
        **kwargs
    )