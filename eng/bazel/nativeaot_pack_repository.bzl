# Repository rule to locate the NativeAOT runtime pack from the NuGet cache.
# This provides the framework assemblies and static libraries that ILC needs
# for NativeAOT compilation (the "SDK pack").
#
# The pack is normally shipped as:
#   microsoft.netcore.app.runtime.nativeaot.{rid}/{version}/
#     runtimes/{rid}/lib/net{ver}/     — framework assemblies (171 DLLs)
#     runtimes/{rid}/native/           — NativeAOT SDK: CoreLib, TypeLoader, etc. + static libs

def _nativeaot_pack_repository_impl(rctx):
    os_name = rctx.os.name.lower()

    if "mac" in os_name or "darwin" in os_name:
        rid = "osx-arm64"
    else:
        rid = "linux-x64"

    # Look for the NativeAOT runtime pack in the NuGet cache
    home = rctx.os.environ.get("HOME", "/root")
    nuget_cache = home + "/.nuget/packages"
    pack_name = "microsoft.netcore.app.runtime.nativeaot." + rid

    pack_dir = rctx.path(nuget_cache + "/" + pack_name)
    if not pack_dir.exists:
        fail("NativeAOT runtime pack not found at %s. Install with: dotnet workload install aot" % str(pack_dir))

    # Find the latest 10.x version
    versions = []
    result = rctx.execute(["ls", str(pack_dir)])
    if result.return_code == 0:
        for line in result.stdout.strip().split("\n"):
            line = line.strip()
            if line.startswith("10."):
                versions.append(line)

    if not versions:
        fail("No .NET 10 NativeAOT runtime pack found in %s" % str(pack_dir))

    # Sort and pick latest
    version = sorted(versions)[-1]

    pack_root = str(pack_dir) + "/" + version + "/runtimes/" + rid

    # Symlink the framework and native directories
    framework_dir = pack_root + "/lib/net10.0"
    native_dir = pack_root + "/native"

    if rctx.path(framework_dir).exists:
        rctx.symlink(framework_dir, "framework")
    else:
        rctx.execute(["mkdir", "-p", "framework"])

    if rctx.path(native_dir).exists:
        rctx.symlink(native_dir, "native")
    else:
        rctx.execute(["mkdir", "-p", "native"])

    # Write the BUILD file with specific library groups
    rctx.file("BUILD.bazel", """
package(default_visibility = ["//visibility:public"])

filegroup(
    name = "framework_files",
    srcs = glob(["framework/*.dll"]),
)

filegroup(
    name = "native_dlls",
    srcs = glob(["native/*.dll"]),
)

# Default set of native libraries for linking a NativeAOT executable.
# This mirrors the MSBuild NativeLibrary items for a standard WorkstationGC,
# eventpipe-disabled build.
filegroup(
    name = "native_libs",
    srcs = [
        "native/libSystem.Native.a",
        "native/libSystem.IO.Compression.Native.a",
        "native/libSystem.Net.Security.Native.a",
        "native/libSystem.Security.Cryptography.Native.OpenSsl.a",
        "native/libbootstrapper.o",
        "native/libRuntime.WorkstationGC.a",
        "native/libeventpipe-enabled.a",
        "native/libRuntime.VxsortEnabled.a",
        "native/libstandalonegc-disabled.a",
        "native/libaotminipal.a",
        "native/libstdc++compat.a",
        "native/libz.a",
        "native/libbrotlienc.a",
        "native/libbrotlidec.a",
        "native/libbrotlicommon.a",
    ],
)

filegroup(
    name = "all_native_libs",
    srcs = glob(["native/*.a", "native/*.o"]),
)
""")

nativeaot_pack_repository = repository_rule(
    implementation = _nativeaot_pack_repository_impl,
    local = True,
    environ = ["HOME"],
    doc = "Locates the NativeAOT runtime pack from the NuGet cache.",
)
