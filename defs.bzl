load("@rules_dotnet//dotnet:defs.bzl", _base_csharp_library="csharp_library")

# The TFM that we're building
NETCOREAPP_CURRENT = "net9.0"
# The TFM used by our LKG SDK
NETCOREAPP_TOOL_CURRENT = "net9.0"

LIVE_REFPACK_DEPS = [
    # Roughly topologically sorted
    "//src/libraries:ref_System.Runtime",
    "//src/libraries:ref_System.Console",
    "//src/libraries:ref_System.Collections",
    "//src/libraries:ref_System.Collections.NonGeneric",
    "//src/libraries:ref_System.ComponentModel",
    "//src/libraries:ref_System.Diagnostics.FileVersionInfo",
    "//src/libraries:ref_System.ObjectModel",
    "//src/libraries:ref_System.ComponentModel.Primitives",
    "//src/libraries:ref_System.Collections.Specialized",
    "//src/libraries:ref_System.Runtime.InteropServices",
    "//src/libraries:ref_System.Diagnostics.Process",
]

def from_coreclr_artifacts(file):
    return select({
        "@platforms//os:linux": [ Label("//:artifacts/bin/coreclr/linux.x64.Debug/%s" % file) ],
        "@platforms//os:macos": [ Label("//:artifacts/bin/coreclr/osx.arm64.Debug/%s" % file) ],
    })

def _gen_resx_source_impl(ctx):
    print(ctx.attr.assembly_name)
    resource_name = "FxResources.%s.SR" % ctx.attr.assembly_name
    ctx.actions.run(
        executable = ctx.executable._exe,
        inputs = [ctx.file.resource_file],
        outputs = [ctx.outputs.out],
        arguments = [
            "--output-path=%s" % ctx.outputs.out.path,
            "--resource-name=%s" % resource_name,
            "--resource-file=%s" % ctx.file.resource_file.path,
        ],
    )

gen_resx_source = rule(
    implementation = _gen_resx_source_impl,
    attrs = {
        "out": attr.output(mandatory = True),
        "assembly_name": attr.string(mandatory = True),
        "resource_file": attr.label(
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

def csharp_library(
    name,
    srcs,
    out = None,
    resource_file = None,
    **kwargs
):
    if out == None:
        out = name

    if resource_file != None:
        resx_target = "resx_" + name
        gen_resx_source(
            name = resx_target,
            out = name + ".System.SR.cs",
            assembly_name = out,
            resource_file = resource_file,
        )
        srcs = srcs + [ ":" + resx_target ]

    _base_csharp_library(
        name = name,
        srcs = srcs,
        out = out,
        **kwargs
    )