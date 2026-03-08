# Precompiled header (PCH) rule for Clang.
#
# Compiles a C++ header into a .pch file.  Consumers use -include-pch
# with $(execpath) to reference the PCH, and list it in
# additional_compiler_inputs so it is available during compilation.
#
# Usage in BUILD:
#
#   load("//:cc_pch.bzl", "cc_pch")
#
#   cc_pch(
#       name = "my_pch",
#       header = "common.h",
#       copts  = CORECLR_COPTS + CLR_CONFIG_COPTS,
#       local_defines = CLR_CONFIG_DEFINES,
#       defines = CORECLR_DEFINES,
#       deps = ["//src/coreclr/inc:coreclr_inc", ...],
#   )
#
#   cc_library(
#       name = "my_lib",
#       copts = [..., "-include-pch", "$(execpath :my_pch)"],
#       additional_compiler_inputs = [":my_pch"],
#       deps  = [...],
#   )

load("@bazel_tools//tools/build_defs/cc:action_names.bzl", "ACTION_NAMES")
load("@bazel_tools//tools/cpp:toolchain_utils.bzl", "find_cpp_toolchain")
load("@rules_cc//cc/common:cc_common.bzl", "cc_common")
load("@rules_cc//cc/common:cc_info.bzl", "CcInfo")

def _normalize_path(path):
    """Resolve '.' and '..' components in a POSIX path."""
    parts = path.split("/")
    result = []
    for part in parts:
        if part == ".":
            continue
        elif part == ".." and result:
            result.pop()
        else:
            result.append(part)
    return "/".join(result)

def _cc_pch_impl(ctx):
    cc_toolchain = find_cpp_toolchain(ctx)
    feature_configuration = cc_common.configure_features(
        ctx = ctx,
        cc_toolchain = cc_toolchain,
        requested_features = ctx.features,
        unsupported_features = ctx.disabled_features,
    )

    header = ctx.file.header

    # Use "./" + path as the source file so that __FILE__ in headers
    # included by the PCH matches the non-PCH behaviour, where
    # ``-include src/.../header.h`` resolves via ``-iquote .`` and Clang
    # records all transitive paths with a ``./`` prefix.
    header_path = "./" + header.path

    # Use the rule name for the output so multiple PCH targets for the same
    # header (with different defines) don't collide.
    pch = ctx.actions.declare_file(ctx.label.name + ".pch")

    compiler = cc_common.get_tool_for_action(
        feature_configuration = feature_configuration,
        action_name = ACTION_NAMES.cpp_compile,
    )

    # Collect compilation context from deps.
    dep_contexts = [
        dep[CcInfo].compilation_context
        for dep in ctx.attr.deps
        if CcInfo in dep
    ]

    include_dirs = depset(
        [_normalize_path(ctx.label.package + "/" + inc) for inc in ctx.attr.includes],
        transitive = [cc.includes for cc in dep_contexts],
        order = "preorder",
    )
    quote_include_dirs = depset(
        transitive = [cc.quote_includes for cc in dep_contexts],
    )
    system_include_dirs = depset(
        transitive = [cc.system_includes for cc in dep_contexts],
    )

    dep_defines = []
    for cc in dep_contexts:
        dep_defines.extend(cc.defines.to_list())

    # Build the full command line using the cc toolchain, which picks up
    # all global --copt / --cxxopt flags from .bazelrc automatically.
    #
    # ctx.fragments.cpp.copts / .cxxopts carry the --copt / --cxxopt flags
    # from .bazelrc that cc_library applies but get_memory_inefficient_command_line
    # does not include on its own.
    bazelrc_flags = ctx.fragments.cpp.copts + ctx.fragments.cpp.cxxopts

    compile_variables = cc_common.create_compile_variables(
        feature_configuration = feature_configuration,
        cc_toolchain = cc_toolchain,
        source_file = header_path,
        output_file = pch.path,
        user_compile_flags = bazelrc_flags + ctx.attr.copts + [
            # Tell Clang the input is a header to precompile.
            "-x", "c++-header",
            # Determinism: don't embed a timestamp in the .pch.
            "-Xclang", "-fno-pch-timestamp",
        ],
        include_directories = include_dirs,
        quote_include_directories = quote_include_dirs,
        system_include_directories = system_include_dirs,
        preprocessor_defines = depset(dep_defines + ctx.attr.local_defines + ctx.attr.defines),
        use_pic = True,
    )

    command_line = cc_common.get_memory_inefficient_command_line(
        feature_configuration = feature_configuration,
        action_name = ACTION_NAMES.cpp_compile,
        variables = compile_variables,
    )

    # Filter out dependency-file flags (-MD -MF <path>) since we don't
    # declare the .d file as an output and Bazel tracks deps itself.
    filtered = []
    skip_next = False
    for arg in command_line:
        if skip_next:
            skip_next = False
            continue
        if arg == "-MD":
            continue
        if arg == "-MF":
            skip_next = True
            continue
        filtered.append(arg)

    dep_headers = [cc.headers for cc in dep_contexts]
    all_inputs = depset(
        [header] + ctx.files.additional_headers,
        transitive = [cc_toolchain.all_files] + dep_headers,
    )

    ctx.actions.run(
        executable = compiler,
        arguments = filtered,
        inputs = all_inputs,
        outputs = [pch],
        mnemonic = "CppPCH",
        progress_message = "Precompiling %s" % header.short_path,
        # Run without sandboxing so that header paths recorded in the PCH
        # are execroot-relative (e.g. ./src/foo/bar.h) rather than absolute
        # sandbox paths (/home/.../.cache/bazel/.../sandbox/linux-sandbox/N/...).
        # Consumer compilations also see the execroot layout, so the paths
        # remain valid.
        execution_requirements = {"no-sandbox": "1"},
    )

    return [DefaultInfo(files = depset([pch]))]

cc_pch = rule(
    implementation = _cc_pch_impl,
    attrs = {
        "header": attr.label(
            allow_single_file = [".h"],
            mandatory = True,
            doc = "The header file to precompile.",
        ),
        "additional_headers": attr.label_list(
            allow_files = True,
            doc = "Extra header files needed during PCH compilation.",
        ),
        "deps": attr.label_list(
            doc = "cc_library deps that provide include paths and defines.",
        ),
        "copts": attr.string_list(
            doc = "Compiler flags (same as the consuming cc_library).",
        ),
        "local_defines": attr.string_list(
            doc = "Defines applied to this target (same as consuming cc_library).",
        ),
        "defines": attr.string_list(
            doc = "Public defines (same as consuming cc_library).",
        ),
        "includes": attr.string_list(
            doc = "Package-relative include dirs (same as consuming cc_library).",
        ),
        "_cc_toolchain": attr.label(
            default = "@bazel_tools//tools/cpp:current_cc_toolchain",
        ),
    },
    toolchains = ["@bazel_tools//tools/cpp:toolchain_type"],
    fragments = ["cpp"],
)
