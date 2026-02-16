// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CompileCommandComparer;

/// <summary>Parsed CLI options.</summary>
sealed class CliOptions
{
    public required string RepoRoot { get; init; }
    public string? CompileCommandsPath { get; init; }
    public string? BazelAqueryPath { get; init; }
    public string? MsBuildBinlogPath { get; init; }
    public required string MappingPath { get; init; }
    public bool JsonOutput { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        string? repoRoot = null;
        string? compileCommands = null;
        string? bazelAquery = null;
        string? msbuildBinlog = null;
        string? mapping = null;
        bool json = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo-root" when i + 1 < args.Length:
                    repoRoot = args[++i];
                    break;
                case "--compile-commands" when i + 1 < args.Length:
                    compileCommands = args[++i];
                    break;
                case "--bazel-aquery" when i + 1 < args.Length:
                    bazelAquery = args[++i];
                    break;
                case "--msbuild-binlog" when i + 1 < args.Length:
                    msbuildBinlog = args[++i];
                    break;
                case "--mapping" when i + 1 < args.Length:
                    mapping = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
                    return null;
            }
        }

        if (mapping is null)
        {
            Console.Error.WriteLine("Error: --mapping is required");
            return null;
        }

        repoRoot ??= FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("Error: could not detect repo root. Use --repo-root.");
            return null;
        }

        return new CliOptions
        {
            RepoRoot = Path.GetFullPath(repoRoot),
            CompileCommandsPath = compileCommands,
            BazelAqueryPath = bazelAquery,
            MsBuildBinlogPath = msbuildBinlog,
            MappingPath = mapping,
            JsonOutput = json,
        };
    }

    static string? FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "global.json")) &&
                File.Exists(Path.Combine(dir, "MODULE.bazel")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
