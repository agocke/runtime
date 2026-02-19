"""Rules for running crossgen2 (ReadyToRun AOT compiler) on managed assemblies."""

load(
    "@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyRuntimeInfo",
)

def _crossgen_corelib_impl(ctx):
    output = ctx.outputs.out

    # Get the IL assembly from the csharp_library provider
    runtime_info = ctx.attr.assembly[DotnetAssemblyRuntimeInfo]
    il_assembly = runtime_info.libs[0]

    args = [
        "--jitpath:%s" % ctx.file.clrjit.path,
        "-o:%s" % output.path,
        "--targetarch:%s" % ctx.attr.target_arch,
        "--targetos:%s" % ctx.attr.target_os,
        "-O",
    ]

    if ctx.attr.verify_type_and_field_layout:
        args.append("--verify-type-and-field-layout")
        args.append("--enable-cached-interface-dispatch-support")

    args.append(il_assembly.path)

    inputs = [il_assembly, ctx.file.clrjit, ctx.file.jitinterface]

    # jitinterface is loaded by name "jitinterface_<arch>" via NativeLibrary.Load
    env = {
        "LD_LIBRARY_PATH": ctx.file.jitinterface.dirname,
    }

    ctx.actions.run(
        executable = ctx.executable._crossgen2,
        inputs = inputs,
        outputs = [output],
        arguments = args,
        env = env,
        mnemonic = "Crossgen2",
        progress_message = "Crossgen2 compiling %s" % il_assembly.short_path,
    )

crossgen_corelib = rule(
    implementation = _crossgen_corelib_impl,
    attrs = {
        "assembly": attr.label(
            mandatory = True,
            providers = [DotnetAssemblyRuntimeInfo],
            doc = "The IL assembly target (csharp_library) to compile with crossgen2.",
        ),
        "out": attr.output(
            mandatory = True,
            doc = "The output R2R assembly.",
        ),
        "target_arch": attr.string(
            default = "x64",
            doc = "Target architecture (x64, arm64, etc.).",
        ),
        "target_os": attr.string(
            default = "linux",
            doc = "Target OS (linux, windows, osx).",
        ),
        "verify_type_and_field_layout": attr.bool(
            default = False,
            doc = "Enable type and field layout verification (Debug/Checked builds).",
        ),
        "clrjit": attr.label(
            mandatory = True,
            allow_single_file = True,
            doc = "The clrjit shared library.",
        ),
        "jitinterface": attr.label(
            mandatory = True,
            allow_single_file = True,
            doc = "The jitinterface shared library.",
        ),
        "_crossgen2": attr.label(
            default = Label("//src/coreclr/tools/aot/crossgen2"),
            cfg = "exec",
            executable = True,
        ),
    },
)
