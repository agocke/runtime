"""Rule to assemble native runtime components into a .NET runtime directory layout.

Produces a tree artifact matching the standard .NET runtime hosting layout:
    {name}/
        dotnet                                          # host executable
        host/fxr/{version}/libhostfxr.so                # framework resolver
        shared/Microsoft.NETCore.App/{version}/          # shared framework
            libcoreclr.so
            libhostpolicy.so
            libSystem.*.so

Shared libraries and executables are stripped during assembly, with debug
symbols extracted to separate .dbg files — matching CMake's
install_with_stripped_symbols() from eng/native/functions.cmake.
"""

def _strip_commands(dst):
    """Generate objcopy commands to split debug symbols, matching CMake strip_symbols().

    Produces:
        {dst}      - stripped binary with .gnu_debuglink
        {dst}.dbg  - extracted debug symbols
    """
    return [
        "chmod u+w \"{dst}\"".format(dst = dst),
        "objcopy --only-keep-debug \"{dst}\" \"{dst}.dbg\"".format(dst = dst),
        "objcopy --strip-debug --strip-unneeded \"{dst}\"".format(dst = dst),
        "objcopy --add-gnu-debuglink=\"{dst}.dbg\" \"{dst}\"".format(dst = dst),
    ]

def _runtime_layout_impl(ctx):
    output_dir = ctx.actions.declare_directory(ctx.label.name)
    version = ctx.attr.version

    inputs = []
    commands = [
        "mkdir -p \"{dir}/host/fxr/{ver}\"".format(dir = output_dir.path, ver = version),
        "mkdir -p \"{dir}/shared/Microsoft.NETCore.App/{ver}\"".format(dir = output_dir.path, ver = version),
    ]

    for dep in ctx.attr.root_files:
        for f in dep.files.to_list():
            inputs.append(f)
            dst = "{dir}/{name}".format(dir = output_dir.path, name = f.basename)
            commands.append("cp \"{src}\" \"{dst}\"".format(src = f.path, dst = dst))
            commands.append("chmod +x \"{dst}\"".format(dst = dst))
            commands.extend(_strip_commands(dst))

    for dep in ctx.attr.fxr_files:
        for f in dep.files.to_list():
            inputs.append(f)
            dst = "{dir}/host/fxr/{ver}/{name}".format(
                dir = output_dir.path,
                ver = version,
                name = f.basename,
            )
            commands.append("cp \"{src}\" \"{dst}\"".format(src = f.path, dst = dst))
            commands.extend(_strip_commands(dst))

    for dep in ctx.attr.framework_files:
        for f in dep.files.to_list():
            inputs.append(f)
            dst = "{dir}/shared/Microsoft.NETCore.App/{ver}/{name}".format(
                dir = output_dir.path,
                ver = version,
                name = f.basename,
            )
            commands.append("cp \"{src}\" \"{dst}\"".format(src = f.path, dst = dst))
            commands.extend(_strip_commands(dst))

    ctx.actions.run_shell(
        inputs = inputs,
        outputs = [output_dir],
        command = " && ".join(commands),
    )

    return [DefaultInfo(files = depset([output_dir]))]

runtime_layout = rule(
    implementation = _runtime_layout_impl,
    attrs = {
        "version": attr.string(
            mandatory = True,
            doc = "Product version (e.g. '11.0.0') used in framework directory paths.",
        ),
        "root_files": attr.label_list(
            allow_files = True,
            doc = "Files placed at the layout root (e.g. dotnet host executable).",
        ),
        "fxr_files": attr.label_list(
            allow_files = True,
            doc = "Files placed under host/fxr/{version}/.",
        ),
        "framework_files": attr.label_list(
            allow_files = True,
            doc = "Files placed under shared/Microsoft.NETCore.App/{version}/.",
        ),
    },
)
