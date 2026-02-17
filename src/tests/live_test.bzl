load("//:defs.bzl", "NETCOREAPP_CURRENT")
load("@bazel_skylib//lib:dicts.bzl", "dicts")
load("@bazel_skylib//rules:common_settings.bzl", "BuildSettingInfo")
load("@rules_dotnet//dotnet/private:providers.bzl",
    "DotnetAssemblyCompileInfo",
    "DotnetAssemblyRuntimeInfo",)
load("@rules_dotnet//dotnet/private/transitions:tfm_transition.bzl", "tfm_transition")
load("@rules_dotnet//dotnet/private/rules/csharp:binary.bzl", "compile_csharp_exe")
load("@rules_dotnet//dotnet/private/rules/csharp/actions:csharp_assembly.bzl", "AssemblyAction")
load("@rules_dotnet//dotnet/private:common.bzl",
    "collect_transitive_runfiles",
    "generate_depsjson",
    "generate_runtimeconfig",
    "get_toolchain",
    "is_core_framework",
    "is_debug",
    "is_standard_framework",
    "to_rlocation_path",)
load("@rules_dotnet//dotnet/private/macros:register_tfms.bzl", "get_tfm_value")
load("//src/libraries:defs.bzl", "live_csharp_library", "LIVE_REFPACK_DEPS")
load("//src/tests:defs.bzl", "COMMON_ATTRS", "build_binary", "create_launcher", "COPY_EXECUTION_REQUIREMENTS")

# Match src/tests/Directory.Build.props NoWarn
_TEST_NOWARN = [
    "CS0078", "CS0162", "CS0164", "CS0168", "CS0169", "CS0219",
    "CS0251", "CS0252", "CS0414", "CS0429", "CS0618", "CS0642",
    "CS0649", "CS0652", "CS0659", "CS0675", "CS1691", "CS1717",
    "CS1718", "CS3001", "CS3002", "CS3003", "CS3005", "CS3008",
    "CS3016", "CS8981",
]

def _live_csharp_test_impl(ctx):
    result = build_binary(ctx, compile_csharp_exe)
    return result

def _to_dict(s):
    return {
        key: getattr(s, key) for key in dir(s)
        if key != "to_json" and key != "to_proto" and key != "aspect_ids"
    }


_live_csharp_test = rule(
    _live_csharp_test_impl,
    doc = """Compile a C# exe for the live framework""",
    attrs = dicts.add(
        COMMON_ATTRS,
        {
            "_launcher_sh": attr.label(
                doc = "A template file for the launcher on Linux/MacOS",
                default = "//eng:run_test.sh.tpl",
                allow_single_file = True,
            ),
        }),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def live_csharp_test(
    name,
    deps = [],
    analyzers = [],
    nowarn = [],
    size = "small",
    **kwargs
):
    analyzers = analyzers + [
        "//src/tests/Common:XUnitWrapperGenerator",
    ]
    deps = deps + LIVE_REFPACK_DEPS
    _live_csharp_test(
        name = name,
        deps = deps,
        analyzers = analyzers,
        target_frameworks = [NETCOREAPP_CURRENT],
        nowarn = nowarn + [ "CS1701" ] + _TEST_NOWARN,
        size = size,
        **kwargs
    )

def _compile_csharp_library(ctx, tfm):
    """Compile action that produces a library instead of exe."""
    toolchain = get_toolchain(ctx)
    return AssemblyAction(
        ctx.actions,
        ctx.executable._compiler_wrapper_bat if ctx.target_platform_has_constraint(ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]) else ctx.executable._compiler_wrapper_sh,
        label = ctx.label,
        additionalfiles = ctx.files.additionalfiles,
        direct_analyzers = ctx.attr.analyzers,
        debug = is_debug(ctx),
        defines = ctx.attr.defines,
        deps = ctx.attr.deps,
        exports = [],
        targeting_pack = ctx.attr._targeting_pack[0],
        internals_visible_to = ctx.attr.internals_visible_to,
        cls_compliant = ctx.attr.cls_compliant,
        assembly_version = ctx.attr.assembly_version,
        keyfile = ctx.file.keyfile,
        langversion = ctx.attr.langversion if ctx.attr.langversion != "" else toolchain.dotnetinfo.csharp_default_version,
        resources = ctx.files.resources,
        resource_logical_names = getattr(ctx.attr, "resource_logical_names", {}),
        srcs = ctx.files.srcs,
        data = ctx.files.data,
        appsetting_files = [],
        compile_data = ctx.files.compile_data,
        out = ctx.attr.out,
        target = "library",
        target_name = ctx.attr.name,
        target_framework = tfm,
        toolchain = toolchain,
        strict_deps = toolchain.strict_deps[BuildSettingInfo].value,
        generate_documentation_file = ctx.attr.generate_documentation_file,
        include_host_model_dll = False,
        treat_warnings_as_errors = ctx.attr.treat_warnings_as_errors,
        warnings_as_errors = ctx.attr.warnings_as_errors,
        warnings_not_as_errors = ctx.attr.warnings_not_as_errors,
        warning_level = ctx.attr.warning_level,
        nowarn = ctx.attr.nowarn,
        project_sdk = ctx.attr.project_sdk,
        allow_unsafe_blocks = ctx.attr.allow_unsafe_blocks,
        nullable = ctx.attr.nullable,
        run_analyzers = ctx.attr.run_analyzers,
        is_analyzer = False,
        is_language_specific_analyzer = False,
        analyzer_configs = ctx.files.analyzer_configs,
        compiler_options = ctx.attr.compiler_options,
        override_debug = False,
        ref_assembly = False,
        is_windows = ctx.target_platform_has_constraint(ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]),
    )

def _xunit_library_test_impl(ctx):
    """Build a library test and create a launcher that runs xunit.console.dll."""
    tfm = get_tfm_value(ctx.attr._target_framework)

    if is_standard_framework(tfm):
        fail("It doesn't make sense to build a test for " + tfm)

    (compile_provider, runtime_provider) = _compile_csharp_library(ctx, tfm)
    dll = runtime_provider.libs[0]
    additional_runfiles = []

    # Copy the xunit console runner files to the same output directory as the test DLL.
    xunit_console_dll = None
    for f in ctx.files._xunit_runner:
        dst = ctx.actions.declare_file("%s/%s/%s" % (ctx.label.name, tfm, f.basename))
        ctx.actions.run_shell(
            inputs = [f],
            outputs = [dst],
            command = "cp -f \"$1\" \"$2\"",
            arguments = [f.path, dst.path],
            mnemonic = "CopyFile",
            progress_message = "Copying %s" % f.basename,
            use_default_shell_env = True,
            execution_requirements = COPY_EXECUTION_REQUIREMENTS,
        )
        additional_runfiles.append(dst)
        if f.basename == "xunit.console.dll":
            xunit_console_dll = dst

    if xunit_console_dll == None:
        fail("xunit.console.dll not found in xunit runner files")

    # Copy transitive runtime deps to the output directory
    transitive_runtime_deps = runtime_provider.deps.to_list()
    for dep in transitive_runtime_deps:
        for lib in dep.libs:
            if lib.extension == "dll":
                dst = ctx.actions.declare_file("%s/%s/%s" % (ctx.label.name, tfm, lib.basename))
                ctx.actions.run_shell(
                    inputs = [lib],
                    outputs = [dst],
                    command = "cp -f \"$1\" \"$2\"",
                    arguments = [lib.path, dst.path],
                    mnemonic = "CopyFile",
                    progress_message = "Copying files",
                    use_default_shell_env = True,
                    execution_requirements = COPY_EXECUTION_REQUIREMENTS,
                )
                additional_runfiles.append(dst)

    # Build a testhost: a directory layout with a shared framework containing
    # Bazel-built assemblies, matching how MSBuild's testhost uses live-built
    # bits. This ensures the real dotnet hosting pipeline is tested, and that
    # DEBUG-built assemblies are loaded instead of the SDK's RELEASE versions.
    toolchain = get_toolchain(ctx)
    dotnet_files = toolchain.dotnetinfo.runtime_files
    dotnet_file = dotnet_files[0]
    sdk_version = toolchain.dotnetinfo.runtime_version

    # RemoteExecutor spawns child processes via "dotnet exec
    # Microsoft.DotNet.RemoteExecutor.dll". Without a runtimeconfig.json the
    # host treats it as self-contained and fails to find libhostpolicy.so.
    # Generate a framework-dependent runtimeconfig so the child resolves the
    # framework (and libhostpolicy) from the testhost.
    _generate_remote_executor_runtimeconfig(ctx, tfm, sdk_version, additional_runfiles)

    testhost = ctx.actions.declare_directory("%s/testhost" % ctx.label.name)
    _build_testhost(ctx, testhost, dotnet_file, sdk_version, additional_runfiles)

    windows_constraint = ctx.attr._windows_constraint[platform_common.ConstraintValueInfo]
    launcher = ctx.actions.declare_file("{}.{}".format(dll.basename, "bat" if ctx.target_platform_has_constraint(windows_constraint) else "sh"), sibling = dll)
    ctx.actions.expand_template(
        template = ctx.file._launcher_sh,
        output = launcher,
        substitutions = {
            "TEMPLATED_testhost": to_rlocation_path(ctx, testhost),
            "TEMPLATED_xunit_console": to_rlocation_path(ctx, xunit_console_dll),
            "TEMPLATED_entry_dll": to_rlocation_path(ctx, dll),
        },
        is_executable = True,
    )
    additional_runfiles.append(testhost)
    additional_runfiles.extend(ctx.files._bash_runfiles)

    default_info = DefaultInfo(
        executable = launcher,
        runfiles = collect_transitive_runfiles(ctx, runtime_provider, ctx.attr.deps).merge(ctx.runfiles(files = additional_runfiles)).merge(ctx.attr._bash_runfiles[DefaultInfo].default_runfiles),
        files = depset([dll]),
    )

    return [default_info, compile_provider, runtime_provider]

def _generate_remote_executor_runtimeconfig(ctx, tfm, sdk_version, additional_runfiles):
    """Generate a runtimeconfig.json for Microsoft.DotNet.RemoteExecutor.

    RemoteExecutor spawns child processes that need this file so the dotnet host
    treats them as framework-dependent and resolves libhostpolicy.so from the
    testhost's shared framework directory.
    """
    has_remote_executor = False
    for f in additional_runfiles:
        if f.basename == "Microsoft.DotNet.RemoteExecutor.dll":
            has_remote_executor = True
            break

    if not has_remote_executor:
        return

    runtimeconfig = ctx.actions.declare_file(
        "%s/%s/Microsoft.DotNet.RemoteExecutor.runtimeconfig.json" % (ctx.label.name, tfm),
    )
    ctx.actions.write(
        output = runtimeconfig,
        content = """\
{{
  "runtimeOptions": {{
    "tfm": "{tfm}",
    "framework": {{
      "name": "Microsoft.NETCore.App",
      "version": "{version}"
    }}
  }}
}}
""".format(tfm = tfm, version = sdk_version),
    )
    additional_runfiles.append(runtimeconfig)

def _build_testhost(ctx, testhost, dotnet_file, sdk_version, test_deps):
    """Assemble a testhost directory with a shared framework from live-built bits.

    Layout:
        testhost/
          dotnet                               (copied from SDK)
          host/fxr/<version>/libhostfxr.so     (from SDK)
          shared/Microsoft.NETCore.App/<version>/
            <SDK assemblies as base>
            <Core_Root assemblies override SDK>
            <test dep assemblies override all>

    The dotnet binary must be a real file (not symlink) inside the testhost
    so that the host resolves frameworks from this directory, not the SDK's.
    """
    core_root = ctx.file._core_root

    # Gather test dep DLLs for the override step.
    test_dep_args = []
    for f in test_deps:
        if f.path.endswith(".dll"):
            test_dep_args.append(f.path)

    ctx.actions.run_shell(
        inputs = [core_root, dotnet_file] + test_deps,
        outputs = [testhost],
        command = """\
SDK_ROOT=$(dirname "$(readlink -f "$1")")
VERSION="$2"
OUT="$3"
CORE_ROOT="$4"
shift 4

FW_DIR="$OUT/shared/Microsoft.NETCore.App/$VERSION"
mkdir -p "$FW_DIR"
mkdir -p "$OUT/host/fxr/$VERSION"

# Copy the dotnet host binary so it resolves frameworks from this directory.
cp -a "$SDK_ROOT/dotnet" "$OUT/dotnet"

# SDK host framework resolver
cp -a "$SDK_ROOT/host/fxr/$VERSION/"* "$OUT/host/fxr/$VERSION/"

# SDK shared framework as base (lowest priority)
cp -a "$SDK_ROOT/shared/Microsoft.NETCore.App/$VERSION/"* "$FW_DIR/"

# Core_Root: Bazel-built runtime + managed assemblies override SDK
cp -af "$CORE_ROOT/"* "$FW_DIR/"

# Test dep assemblies have highest priority
for f in "$@"; do
  cp -af "$f" "$FW_DIR/"
done
""",
        arguments = [dotnet_file.path, sdk_version, testhost.path, core_root.path] + test_dep_args,
        mnemonic = "BuildTestHost",
        progress_message = "Building testhost for %s" % ctx.label.name,
    )

_xunit_library_test = rule(
    _xunit_library_test_impl,
    doc = """Compile a C# library test and run it with xunit.console.dll""",
    attrs = dicts.add(
        COMMON_ATTRS,
        {
            "_launcher_sh": attr.label(
                doc = "A template file for the launcher on Linux/MacOS",
                default = "//eng:run_library_test.sh.tpl",
                allow_single_file = True,
            ),
            "_xunit_runner": attr.label(
                doc = "The xunit console runner files",
                default = "//eng:xunit_console_runner",
                allow_files = True,
            ),
        }),
    test = True,
    toolchains = [
        "@rules_dotnet//dotnet:toolchain_type",
    ],
    cfg = tfm_transition,
)

def library_test(
    name,
    deps = [],
    analyzers = [],
    nowarn = [],
    size = "medium",
    **kwargs
):
    """Test macro for library tests that compiles as library and runs via xunit.console.dll."""
    deps = deps + LIVE_REFPACK_DEPS
    # Match MSBuild default: src/libraries/Directory.Build.props sets
    # <Nullable>annotations</Nullable> for test projects.
    nullable = kwargs.pop("nullable", "annotations")
    _xunit_library_test(
        name = name,
        deps = deps,
        analyzers = analyzers,
        target_frameworks = [NETCOREAPP_CURRENT],
        nowarn = nowarn + [ "CS1701" ] + _TEST_NOWARN,
        size = size,
        nullable = nullable,
        **kwargs
    )

def coreclr_test(
    name,
    deps = [],
    size = "small",
    pri = 0,
    tags = [],
    debug_type = "portable", # TODO: plum through to compiler
    optimize = False, # TODO: plum through to compiler
    compiler_options = [],
    **kwargs
):
    deps = deps + [
        "@paket.main//microsoft.dotnet.xunitassert",
        "@paket.main//xunit.abstractions",
        "@paket.main//xunit.extensibility.core",
    ]

    compiler_options = [
        "/debug:%s" % debug_type,
        "/optimize%s" % ("" if optimize else "-"),
    ] + compiler_options

    # Create two targets: a library for the merged runner and a test. We'll use one or the other.
    live_csharp_library(
        name = name + "_lib",
        deps = deps,
        nowarn = _TEST_NOWARN,
        tags = tags,
        visibility = ["//visibility:public"],
        compiler_options = compiler_options,
        **kwargs
    )

    live_csharp_test(
        name = name,
        deps = deps,
        size = size,
        tags = tags + [ "pri%d" % pri ],
        compiler_options = compiler_options,
        **kwargs
    )

def _transform_dep_impl(ctx):
    # Transform explicit dep into dep with extern alias
    dep = ctx.attr.dep
    compile = dep[DotnetAssemblyCompileInfo]
    compile_dict = _to_dict(compile)
    compile_dict.pop("alias")
    newcomp = DotnetAssemblyCompileInfo(
        alias = "_" + compile.name.replace(".", "_"),
        **compile_dict
    )
    default_info = dep[DefaultInfo]
    runtime_info = dep[DotnetAssemblyRuntimeInfo]
    return [
        default_info,
        newcomp,
        runtime_info,
    ]

_transform_dep = rule(
    _transform_dep_impl,
    attrs = {
        "dep": attr.label(
            doc = "The dependencies to transform",
            providers = [DotnetAssemblyCompileInfo],
        ),
    },
)

def _il_test_impl(ctx):
    args = []
    if ctx.attr.debug_type == "full":
        args.append("-debug")
    if ctx.attr.debug_type == "pdbonly":
        args.append("-debug=opt")
    if ctx.attr.optimize:
        args.append("-optimize")

    args.append("-output=%s" % ctx.outputs.out.path)

    for src in ctx.files.srcs:
        args.append(src.path)

    dll = ctx.outputs.out
    additional_runfiles = [dll]

    ctx.actions.run(
        inputs = ctx.files.srcs,
        outputs = [ctx.outputs.out],
        arguments = args,
        progress_message = "Compiling %s" % ctx.outputs.out.short_path,
        executable = ctx.executable.ilasm_exe,
    )

    launcher = create_launcher(ctx, additional_runfiles, dll)

    default_info = DefaultInfo(
        executable = launcher,
        runfiles = ctx.runfiles(files = additional_runfiles).merge(ctx.attr._bash_runfiles[DefaultInfo].default_runfiles),
        files = depset([dll]),
    )

    return [ default_info ]

_il_test = rule(
    implementation = _il_test_impl,
    attrs = {
        "srcs": attr.label_list(
            doc = "The source files to compile",
            allow_files = True,
        ),
        "out": attr.output(
            mandatory = True,
            doc = "The output DLL.",
        ),
        "debug_type": attr.string(
            doc = "The debug type",
            default = "full",
        ),
        "optimize": attr.bool(
            doc = "Enable optimization.",
            default = False,
        ),
        "ilasm_exe": attr.label(
            default = Label("//src/coreclr/ilasm"),
            cfg = "exec",
            executable = True,
            allow_files = True,
        ),
        "_launcher_sh": attr.label(
            doc = "A template file for the launcher on Linux/MacOS",
            default = "//eng:run_test.sh.tpl",
            allow_single_file = True,
        ),
        "_windows_constraint": attr.label(default = "@platforms//os:windows"),
        "_core_root": attr.label(
            doc = "The host binary to use for the launcher",
            default = "//:Core_Root",
            allow_single_file = True,
        ),
        "_bash_runfiles": attr.label(
            default = "@bazel_tools//tools/bash/runfiles",
            allow_files = True,
        ),
    },
    test = True,
)

def il_coreclr_test(
    name,
    size = "small",
    pri = 0,
    tags = [],
    **kwargs
):
    _il_test(
        name = name,
        out = name + ".dll",
        size = size,
        tags = tags + [ "pri%d" % pri ],
        **kwargs
    )


def coreclr_merged_test(
    name,
    deps = [],
    test_deps = [],
    size = "medium",
    tags = [],
    **kwargs
):
    """ Create a merged test that includes all of the test_deps as test sources.

    Args:
        name: The name of the test
        deps: The dependencies of the test
        test_deps: The test dependencies to merge
        tags: The tags for the test
        **kwargs: Additional arguments to pass to live_csharp_test
    """

    # Tests may have the same types, so we need to add extern aliases
    transformed_deps = []
    for (i, dep) in enumerate(test_deps):
        transform_label_name = "_transform_dep_%s_%s" % (name, i)
        # coreclr_test creates two targets, one library and one test. We need the library target as
        # a dependency.
        dep_label = native.package_relative_label(dep)
        lib_dep =  dep_label.same_package_label(dep_label.name + "_lib")

        _transform_dep(
            name = transform_label_name,
            dep = lib_dep,
        )
        transformed_deps.append(":" + transform_label_name)

    live_csharp_test(
        name = name,
        deps = deps + transformed_deps,
        size = size,
        tags = tags + ["merged", "manual"],
        **kwargs
    )