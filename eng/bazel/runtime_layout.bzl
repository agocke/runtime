"""Rule to assemble native runtime components into a .NET runtime directory layout.

Produces a tree artifact matching the standard .NET runtime hosting layout:
    {name}/
        dotnet                                          # host executable
        host/fxr/{version}/libhostfxr.so                # framework resolver
        shared/Microsoft.NETCore.App/{version}/          # shared framework
            libcoreclr.so
            libhostpolicy.so
            libSystem.*.so
"""

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

    for dep in ctx.attr.fxr_files:
        for f in dep.files.to_list():
            inputs.append(f)
            commands.append("cp \"{src}\" \"{dir}/host/fxr/{ver}/{name}\"".format(
                src = f.path,
                dir = output_dir.path,
                ver = version,
                name = f.basename,
            ))

    for dep in ctx.attr.framework_files:
        for f in dep.files.to_list():
            inputs.append(f)
            commands.append("cp \"{src}\" \"{dir}/shared/Microsoft.NETCore.App/{ver}/{name}\"".format(
                src = f.path,
                dir = output_dir.path,
                ver = version,
                name = f.basename,
            ))

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
