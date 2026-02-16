// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CompileCommandComparer;

/// <summary>
/// Normalizes paths and classifies compiler arguments.
/// </summary>
static class Normalizer
{
    // Prefixes to strip from absolute paths to get repo-relative paths.
    static readonly string[] s_bazelPrefixes =
    [
        "bazel-out/k8-dbg/bin/",
        "bazel-out/k8-opt/bin/",
        "bazel-out/k8-fastbuild/bin/",
        "bazel-out/",
    ];

    // Defines that differ by design between build systems and should be ignored.
    static readonly HashSet<string> s_ignoreDefines = new(StringComparer.Ordinal)
    {
        "__DATE__=\"redacted\"",       // Bazel deterministic build
        "__TIME__=\"redacted\"",       // Bazel deterministic build
        "__TIMESTAMP__=\"redacted\"",  // Bazel deterministic build
        "-U_FORTIFY_SOURCE",          // Bazel default, not a real define difference
    };

    // Flags that differ by design between build systems and should be ignored.
    static readonly HashSet<string> s_ignoreFlags = new(StringComparer.Ordinal)
    {
        "-c",          // compile-only (implicit)
        "-MD",         // depfile generation
        "-MQ",         // depfile target
        "-MF",         // depfile path
        "-o",          // output path
        "--target",    // triple (differs in form)
        "-fcolor-diagnostics",
        "-fno-canonical-system-headers",
        "-no-canonical-prefixes",
        "-Wno-builtin-macro-redefined",  // Bazel uses this alongside __DATE__=redacted
    };

    // Prefixes of flags that take a value as the next arg and should be ignored.
    static readonly string[] s_ignoreFlagPrefixes =
    [
        "-o",
        "-MF",
        "-MQ",
        "--target=",
        "-fdiagnostics-color",
        "-frandom-seed=",      // Bazel per-file random seed, not a real compilation difference
    ];

    /// <summary>Normalize a file path to a repo-relative form.</summary>
    public static string NormalizePath(string path, string repoRoot)
    {
        // Strip Bazel sandbox prefixes
        foreach (var prefix in s_bazelPrefixes)
        {
            int idx = path.IndexOf(prefix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                path = path[(idx + prefix.Length)..];
                break;
            }
        }

        // Strip absolute repo root prefix
        if (path.StartsWith(repoRoot, StringComparison.Ordinal))
        {
            path = path[repoRoot.Length..].TrimStart('/');
        }

        // Strip artifacts/obj intermediates prefix
        const string artifactsPrefix = "artifacts/obj/";
        if (path.StartsWith(artifactsPrefix, StringComparison.Ordinal))
        {
            // e.g. artifacts/obj/coreclr/linux.x64.Debug/src/... -> src/...
            int srcIdx = path.IndexOf("/src/", StringComparison.Ordinal);
            if (srcIdx >= 0)
                path = path[(srcIdx + 1)..];
        }

        // Normalize external/ paths for Bazel
        const string externalPrefix = "external/";
        if (path.StartsWith(externalPrefix, StringComparison.Ordinal))
        {
            // Keep as-is but normalized
        }

        return path;
    }

    /// <summary>
    /// Parse a flat argument list into categorized sets.
    /// </summary>
    public static (SortedSet<string> defines, SortedSet<string> includes, SortedSet<string> flags)
        ClassifyArgs(IReadOnlyList<string> args, string repoRoot)
    {
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var includes = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            // Skip the compiler executable itself
            if (i == 0 && (arg.Contains("clang") || arg.Contains("gcc") || arg.Contains("cc")))
                continue;

            // Skip ignored flags
            if (s_ignoreFlags.Contains(arg))
            {
                // If this flag takes a value, skip the next arg too
                if (arg is "-o" or "-MF" or "-MQ")
                    i++;
                continue;
            }

            if (s_ignoreFlagPrefixes.Any(p => arg.StartsWith(p, StringComparison.Ordinal)))
                continue;

            // Defines
            if (arg.StartsWith("-D", StringComparison.Ordinal))
            {
                string define = arg.Length > 2 ? arg[2..] : (i + 1 < args.Count ? args[++i] : "");
                if (define.Length > 0 && !s_ignoreDefines.Contains(define))
                    defines.Add(define);
                continue;
            }

            // Undefines (track as -UFOO)
            if (arg.StartsWith("-U", StringComparison.Ordinal))
            {
                string undef = arg.Length > 2 ? arg[2..] : (i + 1 < args.Count ? args[++i] : "");
                string undefKey = $"-U{undef}";
                if (undef.Length > 0 && !s_ignoreDefines.Contains(undefKey))
                    defines.Add(undefKey);
                continue;
            }

            // Include paths
            if (arg.StartsWith("-I", StringComparison.Ordinal))
            {
                string path = arg.Length > 2 ? arg[2..] : (i + 1 < args.Count ? args[++i] : "");
                if (path.Length > 0)
                    includes.Add(NormalizePath(path, repoRoot));
                continue;
            }
            if (arg == "-isystem" && i + 1 < args.Count)
            {
                includes.Add("(system)" + NormalizePath(args[++i], repoRoot));
                continue;
            }
            if (arg.StartsWith("-isystem", StringComparison.Ordinal))
            {
                includes.Add("(system)" + NormalizePath(arg[8..], repoRoot));
                continue;
            }
            if (arg.StartsWith("-iquote", StringComparison.Ordinal))
            {
                string path = arg.Length > 7 ? arg[7..] : (i + 1 < args.Count ? args[++i] : "");
                if (path.Length > 0)
                    includes.Add("(quote)" + NormalizePath(path, repoRoot));
                continue;
            }

            // Source files (skip)
            if (arg.EndsWith(".c", StringComparison.Ordinal) ||
                arg.EndsWith(".cpp", StringComparison.Ordinal) ||
                arg.EndsWith(".cc", StringComparison.Ordinal) ||
                arg.EndsWith(".S", StringComparison.Ordinal) ||
                arg.EndsWith(".s", StringComparison.Ordinal))
            {
                continue;
            }

            // Everything else is a compiler flag
            flags.Add(arg);
        }

        return (defines, includes, flags);
    }

    /// <summary>
    /// Classify MSBuild Csc arguments into defines and references.
    /// </summary>
    public static (SortedSet<string> defines, SortedSet<string> references, SortedSet<string> flags)
        ClassifyCscArgs(IReadOnlyList<string> args, string repoRoot)
    {
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string arg in args)
        {
            if (arg.StartsWith("/define:", StringComparison.Ordinal) ||
                arg.StartsWith("-define:", StringComparison.Ordinal))
            {
                string defs = arg[8..];
                foreach (string d in defs.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    defines.Add(d);
                continue;
            }

            if (arg.StartsWith("/reference:", StringComparison.Ordinal) ||
                arg.StartsWith("-reference:", StringComparison.Ordinal) ||
                arg.StartsWith("/r:", StringComparison.Ordinal) ||
                arg.StartsWith("-r:", StringComparison.Ordinal))
            {
                string refPath = arg.Contains(':') ? arg[(arg.IndexOf(':') + 1)..] : arg;
                // Normalize to just assembly name
                string asmName = Path.GetFileNameWithoutExtension(refPath);
                references.Add(asmName);
                continue;
            }

            // Skip output, source file args
            if (arg.StartsWith("/out:", StringComparison.Ordinal) ||
                arg.StartsWith("-out:", StringComparison.Ordinal) ||
                arg.EndsWith(".cs", StringComparison.Ordinal))
            {
                continue;
            }

            flags.Add(arg);
        }

        return (defines, references, flags);
    }
}
