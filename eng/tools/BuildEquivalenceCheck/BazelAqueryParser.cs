// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace BuildEquivalenceCheck;

/// <summary>
/// Parses Bazel aquery JSON output into normalized compilation records.
/// Handles both CppCompile (native) and CSharpCompile (managed) actions.
/// </summary>
public static class BazelAqueryParser
{
    public static List<NativeCompilationRecord> ParseNativeActions(string aqueryJsonPath, string repoRoot)
    {
        var records = new List<NativeCompilationRecord>();
        using var doc = JsonDocument.Parse(File.ReadAllText(aqueryJsonPath));
        var root = doc.RootElement;

        var targets = ParseTargets(root);
        var actions = root.GetProperty("actions");

        foreach (var action in actions.EnumerateArray())
        {
            var mnemonic = action.GetProperty("mnemonic").GetString();
            if (mnemonic is not "CppCompile")
                continue;

            var targetId = action.GetProperty("targetId").ToString();
            var targetLabel = targets.GetValueOrDefault(targetId, "");
            var args = ParseArguments(action);
            var record = ParseNativeArguments(args, targetLabel, repoRoot);
            if (record is not null)
                records.Add(record);
        }

        return records;
    }

    public static List<ManagedCompilationRecord> ParseManagedActions(string aqueryJsonPath, string repoRoot)
    {
        var records = new List<ManagedCompilationRecord>();
        using var doc = JsonDocument.Parse(File.ReadAllText(aqueryJsonPath));
        var root = doc.RootElement;

        var targets = ParseTargets(root);
        var actions = root.GetProperty("actions");

        foreach (var action in actions.EnumerateArray())
        {
            var mnemonic = action.GetProperty("mnemonic").GetString();
            if (mnemonic is not "CSharpCompile")
                continue;

            var targetId = action.GetProperty("targetId").ToString();
            var targetLabel = targets.GetValueOrDefault(targetId, "");
            var args = ParseArguments(action);
            var record = ParseManagedArguments(args, targetLabel, repoRoot);
            if (record is not null)
                records.Add(record);
        }

        return records;
    }

    private static Dictionary<string, string> ParseTargets(JsonElement root)
    {
        var targets = new Dictionary<string, string>();
        if (root.TryGetProperty("targets", out var targetsArray))
        {
            foreach (var t in targetsArray.EnumerateArray())
            {
                var id = t.GetProperty("id").ToString();
                var label = t.GetProperty("label").GetString() ?? "";
                targets[id] = label;
            }
        }

        return targets;
    }

    private static List<string> ParseArguments(JsonElement action)
    {
        var args = new List<string>();
        if (action.TryGetProperty("arguments", out var argsArray))
        {
            foreach (var arg in argsArray.EnumerateArray())
            {
                args.Add(arg.GetString() ?? "");
            }
        }

        return args;
    }

    private static NativeCompilationRecord? ParseNativeArguments(List<string> args, string targetLabel, string repoRoot)
    {
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var undefines = new SortedSet<string>(StringComparer.Ordinal);
        var includes = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        string langStd = "";
        string optLevel = "";
        string? sourceFile = null;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("-D"))
            {
                defines.Add(arg[2..]);
            }
            else if (arg.StartsWith("-U"))
            {
                undefines.Add(arg[2..]);
            }
            else if (arg == "-isystem" || arg == "-iquote")
            {
                if (i + 1 < args.Count)
                    includes.Add(NormalizeBazelPath(args[++i], repoRoot));
            }
            else if (arg.StartsWith("-I"))
            {
                var path = arg.Length > 2 ? arg[2..] : (i + 1 < args.Count ? args[++i] : "");
                includes.Add(NormalizeBazelPath(path, repoRoot));
            }
            else if (arg.StartsWith("-std="))
            {
                langStd = arg;
            }
            else if (arg.StartsWith("-O"))
            {
                optLevel = arg;
            }
            else if (arg == "-c" && i + 1 < args.Count)
            {
                sourceFile = NormalizeBazelPath(args[++i], repoRoot);
            }
            else if (arg == "-o" || arg == "-MF" || arg == "-MD" || arg == "-MQ" || arg == "-MT" || arg == "-frandom-seed")
            {
                if (i + 1 < args.Count)
                    i++; // skip argument
            }
            else if (arg.StartsWith("-frandom-seed="))
            {
                // skip
            }
            else if (arg.StartsWith("-W") || arg.StartsWith("-f") || arg == "-g" || arg.StartsWith("-g"))
            {
                flags.Add(arg);
            }
        }

        if (sourceFile is null)
            return null;

        return new NativeCompilationRecord
        {
            SourceFile = sourceFile,
            Target = targetLabel,
            Defines = defines,
            Undefines = undefines,
            IncludePaths = includes,
            Flags = flags,
            LanguageStandard = langStd,
            OptimizationLevel = optLevel,
            BuildSystem = "bazel",
        };
    }

    private static ManagedCompilationRecord? ParseManagedArguments(List<string> args, string targetLabel, string repoRoot)
    {
        var sourceFiles = new SortedSet<string>(StringComparer.Ordinal);
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var noWarn = new SortedSet<string>(StringComparer.Ordinal);
        var analyzers = new SortedSet<string>(StringComparer.Ordinal);
        var cscFlags = new SortedSet<string>(StringComparer.Ordinal);
        string targetType = "library";
        string langVersion = "";
        string? assemblyName = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("/define:"))
            {
                foreach (var d in arg[8..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                    defines.Add(d);
            }
            else if (arg.StartsWith("/nowarn:"))
            {
                foreach (var w in arg[8..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    noWarn.Add(w);
            }
            else if (arg.StartsWith("-r:") || arg.StartsWith("/r:") || arg.StartsWith("/reference:") || arg.StartsWith("-reference:"))
            {
                var refPath = arg[(arg.IndexOf(':') + 1)..];
                references.Add(ExtractAssemblyName(refPath));
            }
            else if (arg.StartsWith("/analyzer:"))
            {
                analyzers.Add(ExtractAssemblyName(arg[10..]));
            }
            else if (arg.StartsWith("/target:"))
            {
                targetType = arg[8..];
            }
            else if (arg.StartsWith("/langversion:"))
            {
                langVersion = arg[13..];
            }
            else if (arg.StartsWith("/out:"))
            {
                assemblyName = ExtractAssemblyName(arg[5..]);
            }
            else if (arg.StartsWith('/'))
            {
                // Other csc flags
                cscFlags.Add(arg);
            }
            else if (!arg.StartsWith('-') && (arg.EndsWith(".cs") || arg.Contains(".cs")))
            {
                sourceFiles.Add(NormalizeBazelPath(arg, repoRoot));
            }
        }

        if (assemblyName is null)
            return null;

        return new ManagedCompilationRecord
        {
            AssemblyName = assemblyName,
            SourceFiles = sourceFiles,
            Defines = defines,
            References = references,
            NoWarn = noWarn,
            Analyzers = analyzers,
            Flags = cscFlags,
            TargetType = targetType,
            LangVersion = langVersion,
            BuildSystem = "bazel",
        };
    }

    private static string ExtractAssemblyName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName;
    }

    internal static string NormalizeBazelPath(string path, string repoRoot)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Bazel paths are relative to execution root.
        // Strip "bazel-out/<config>/bin/" prefix or "external/" prefix.
        if (path.StartsWith("bazel-out/"))
        {
            var parts = path.Split('/', 4);
            if (parts.Length >= 4 && parts[2] == "bin")
                return parts[3];
        }

        if (path.StartsWith("external/"))
            return path;

        // Already repo-relative
        return path;
    }
}
