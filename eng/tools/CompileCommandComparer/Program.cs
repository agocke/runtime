// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using CompileCommandComparer;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

var options = CliOptions.Parse(args);
if (options is null)
    return 2;

var mapping = TargetMappingConfig.Load(options.MappingPath);
if (mapping is null)
{
    Console.Error.WriteLine($"Error: could not load mapping config from '{options.MappingPath}'");
    return 2;
}

var results = new List<TargetComparisonResult>();

// --- Native (C/C++) comparisons ---
if (options.CompileCommandsPath is not null && options.BazelAqueryPath is not null)
{
    Console.WriteLine("=== Native (C/C++) Comparison ===");
    Console.WriteLine();

    var cmakeTargets = CMakeParser.Parse(options.CompileCommandsPath, options.RepoRoot);
    var bazelTargets = BazelNativeParser.Parse(options.BazelAqueryPath, options.RepoRoot);

    foreach (var entry in mapping.Native)
    {
        var cmake = Matcher.FindCMakeTarget(cmakeTargets, entry.CMakeOutputPattern);
        var bazel = Matcher.FindBazelTarget(bazelTargets, entry.BazelLabel);
        var result = Comparator.Compare(entry.Name, cmake, bazel, options.RepoRoot);
        results.Add(result);
    }
}

// --- Managed (C#) comparisons ---
if (options.MsBuildBinlogPath is not null && options.BazelAqueryPath is not null)
{
    Console.WriteLine("=== Managed (C#) Comparison ===");
    Console.WriteLine();

    var msbuildTargets = MsBuildParser.Parse(options.MsBuildBinlogPath, options.RepoRoot);
    var bazelTargets = BazelCSharpParser.Parse(options.BazelAqueryPath, options.RepoRoot);

    foreach (var entry in mapping.Managed)
    {
        var msbuild = Matcher.FindMsBuildTarget(msbuildTargets, entry.MsBuildProject, options.RepoRoot);
        var bazel = Matcher.FindBazelTarget(bazelTargets, entry.BazelLabel);
        var result = Comparator.Compare(entry.Name, msbuild, bazel, options.RepoRoot);
        results.Add(result);
    }
}

// --- Report ---
int exitCode = Reporter.Report(results, options.JsonOutput);
return exitCode;

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: CompileCommandComparer [options]

        Options:
          --repo-root <path>              Repository root (default: auto-detect)
          --compile-commands <path>       Path to CMake compile_commands.json
          --bazel-aquery <path>           Path to Bazel aquery JSON output
          --msbuild-binlog <path>         Path to MSBuild .binlog file
          --mapping <path>                Path to target mapping JSON config
          --json                          Output results as JSON
          -h, --help                      Show this help
        """);
}
