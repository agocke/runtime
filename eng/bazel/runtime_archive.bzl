"""Rule to assemble a .NET runtime directory layout matching the MSBuild packs output.

Produces a tree artifact with the standard runtime layout:
    ./dotnet                                            # host executable
    ./LICENSE.txt                                       # license
    ./ThirdPartyNotices.txt                             # third-party notices
    ./host/fxr/{version}/libhostfxr.so                  # framework resolver
    ./shared/Microsoft.NETCore.App/{version}/            # shared framework
        libcoreclr.so, libclrjit.so, ...                    # native runtime
        System.Private.CoreLib.dll, System.Runtime.dll, ... # managed framework
        Microsoft.NETCore.App.deps.json                     # framework manifest
        Microsoft.NETCore.App.runtimeconfig.json            # runtime config

Native binaries are stripped with objcopy and debug symbols extracted to .dbg
files — matching CMake's install_with_stripped_symbols() from
eng/native/functions.cmake.

To produce a tar.gz archive, compose with tar() from @aspect_bazel_lib//lib:tar.bzl.
"""

def _strip_commands(dst):
    """Generate platform-aware strip commands for native binaries.

    On Linux: objcopy to split debug symbols into .dbg, strip binary,
              and add .gnu_debuglink (matches CMake strip_symbols).
    On macOS: dsymutil + strip (objcopy is not available).
    """
    return [
        "chmod u+w \"{dst}\"".format(dst = dst),
        ("if [ \"$(uname)\" = \"Darwin\" ]; then " +
            "dsymutil \"{dst}\" -o \"{dst}.dSYM\" 2>/dev/null || true && " +
            "strip -x \"{dst}\"; " +
        "else " +
            "objcopy --only-keep-debug \"{dst}\" \"{dst}.dbg\" && " +
            "objcopy --strip-debug --strip-unneeded \"{dst}\" && " +
            "objcopy --add-gnu-debuglink=\"{dst}.dbg\" \"{dst}\" && " +
            "rm -f \"{dst}.dbg\"; " +
        "fi").format(dst = dst),
    ]

def _runtime_staging_impl(ctx):
    version = ctx.attr.version
    output = ctx.actions.declare_directory(ctx.label.name)

    fxr_dir = "host/fxr/{}".format(version)
    fx_dir = "shared/Microsoft.NETCore.App/{}".format(version)

    inputs = []
    commands = [
        'set -euo pipefail',
        'mkdir -p "{out}/{fxr}" "{out}/{fx}"'.format(out = output.path, fxr = fxr_dir, fx = fx_dir),
    ]

    # Root files: dotnet host
    for dep in ctx.attr.root_files:
        for f in dep.files.to_list():
            inputs.append(f)
            dst = '{out}/{name}'.format(out = output.path, name = f.basename)
            commands.append('cp "{src}" "{dst}"'.format(src = f.path, dst = dst))
            commands.append('chmod 755 "{dst}"'.format(dst = dst))
            commands.extend(_strip_commands(dst))

    # License files (with rename support)
    for src_label, dest_name in ctx.attr.license_files.items():
        for f in src_label.files.to_list():
            inputs.append(f)
            commands.append('cp "{src}" "{out}/{name}"'.format(
                src = f.path,
                out = output.path,
                name = dest_name,
            ))
            commands.append('chmod 644 "{out}/{name}"'.format(out = output.path, name = dest_name))

    # Host FXR files (644 in MSBuild archives — .so but not executable)
    for dep in ctx.attr.fxr_files:
        for f in dep.files.to_list():
            inputs.append(f)
            dst = '{out}/{fxr}/{name}'.format(out = output.path, fxr = fxr_dir, name = f.basename)
            commands.append('cp "{src}" "{dst}"'.format(src = f.path, dst = dst))
            commands.append('chmod 644 "{dst}"'.format(dst = dst))
            commands.extend(_strip_commands(dst))

    # Native framework files (shared libs, executables)
    for dep in ctx.attr.framework_native_files:
        for f in dep.files.to_list():
            inputs.append(f)
            dst = '{out}/{fx}/{name}'.format(out = output.path, fx = fx_dir, name = f.basename)
            commands.append('cp "{src}" "{dst}"'.format(src = f.path, dst = dst))
            # Executables get 755, shared libs get 755 (CMake convention)
            commands.append('chmod 755 "{dst}"'.format(dst = dst))
            commands.extend(_strip_commands(dst))

    # Managed framework files (DLLs)
    # Process renames first to build the rename map
    rename_map = {}
    for src_name, dest_name in ctx.attr.rename_files.items():
        rename_map[src_name] = dest_name

    exclude_set = {name: True for name in ctx.attr.exclude_managed_files}

    for dep in ctx.attr.framework_managed_files:
        for f in dep.files.to_list():
            if f.extension == "dll":
                if f.basename in exclude_set:
                    continue
                inputs.append(f)
                dest_name = rename_map.get(f.basename, f.basename)
                # Use cp -f to allow later entries to overwrite earlier ones
                # (e.g. R2R CoreLib overwrites IL CoreLib).
                commands.append('cp -f "{src}" "{out}/{fx}/{name}"'.format(
                    src = f.path,
                    out = output.path,
                    fx = fx_dir,
                    name = dest_name,
                ))
                commands.append('chmod 644 "{out}/{fx}/{name}"'.format(
                    out = output.path,
                    fx = fx_dir,
                    name = dest_name,
                ))

    # Config/data files placed directly into the framework directory
    for dep in ctx.attr.framework_data_files:
        for f in dep.files.to_list():
            inputs.append(f)
            commands.append('cp "{src}" "{out}/{fx}/{name}"'.format(
                src = f.path,
                out = output.path,
                fx = fx_dir,
                name = f.basename,
            ))
            commands.append('chmod 644 "{out}/{fx}/{name}"'.format(
                out = output.path,
                fx = fx_dir,
                name = f.basename,
            ))

    ctx.actions.run_shell(
        inputs = inputs,
        outputs = [output],
        command = "\n".join(commands),
    )

    return [DefaultInfo(files = depset([output]))]

runtime_staging = rule(
    implementation = _runtime_staging_impl,
    attrs = {
        "version": attr.string(
            mandatory = True,
            doc = "Product version string used in paths and archive name (e.g. '10.0.4-dev').",
        ),
        "root_files": attr.label_list(
            allow_files = True,
            doc = "Files at the archive root (e.g. dotnet host executable).",
        ),
        "license_files": attr.label_keyed_string_dict(
            allow_files = True,
            doc = "License files mapped to their archive names (e.g. LICENSE.TXT -> LICENSE.txt).",
        ),
        "fxr_files": attr.label_list(
            allow_files = True,
            doc = "Files under host/fxr/{version}/.",
        ),
        "framework_native_files": attr.label_list(
            allow_files = True,
            doc = "Native files under shared/Microsoft.NETCore.App/{version}/.",
        ),
        "framework_managed_files": attr.label_list(
            allow_files = True,
            doc = "Managed DLLs under shared/Microsoft.NETCore.App/{version}/ (only .dll files are included).",
        ),
        "exclude_managed_files": attr.string_list(
            doc = "DLL basenames to exclude from framework_managed_files (e.g. assemblies in impl_netcoreapp not in the runtime pack).",
        ),
        "rename_files": attr.string_dict(
            doc = "Map of source filename -> destination filename for renames (e.g. R2R CoreLib).",
        ),
        "framework_data_files": attr.label_list(
            allow_files = True,
            doc = "Data/config files under shared/Microsoft.NETCore.App/{version}/ (deps.json, runtimeconfig.json).",
        ),
    },
)
