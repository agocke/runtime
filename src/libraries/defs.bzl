load(
    "@rules_dotnet//dotnet:defs.bzl",
    "csharp_library"
)

load(
    "//:defs.bzl",
    "NETCOREAPP_CURRENT"
)

# Convenience macro for defining a ref assembly for the NetCoreApp framework. The name of the ref
# assembly will be prefixed with "ref_".
def netcoreapp_ref_assembly(
    name,
    srcs,
    deps = [],
    nowarn = [],
    compiler_options = [],
    **kwargs
):
    compiler_options = compiler_options + [
        "/keyfile:/home/andy/.nuget/packages/microsoft.dotnet.arcade.sdk/9.0.0-beta.24423.2/tools/snk/MSFT.snk",
        "/checksumalgorithm:SHA256",
        "/publicsign+",
    ]
    nowarn = nowarn + [
        "CS0169",
    ]
    csharp_library(
        name = "ref_" + name,
        out = name,
        srcs = srcs,
        deps = deps,
        assembly_version = "9.0.0.0",
        visibility = [ "//visibility:public" ],
        nullable = "annotations",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        nowarn = nowarn,
        **kwargs
    )
