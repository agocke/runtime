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
        keyfile = "@@_main~main_extension~nuget.microsoft.dotnet.arcade.sdk.v9.0.0-beta.24423.2//:tools/snk/MSFT.snk",
        target_frameworks = [ NETCOREAPP_CURRENT ],
        disable_implicit_framework_refs = True,
        nowarn = nowarn,
        compiler_options = compiler_options,
        **kwargs
    )

def netcoreapp_impl_assembly(
    name,
    srcs,
    deps = [],
    compiler_options = [],
    **kwargs
):
    compiler_options = compiler_options + [
        "/checksumalgorithm:SHA256",
        "/publicsign+",
    ]
    csharp_library(
        name = "impl_" + name,
        out = name,
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