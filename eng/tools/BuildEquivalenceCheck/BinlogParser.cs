// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Logging.StructuredLogger;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BuildEquivalenceCheck;

/// <summary>
/// Parses MSBuild binary log (.binlog) files to extract Csc task invocations.
/// </summary>
public static class BinlogParser
{
    public static List<ManagedCompilationRecord> Parse(string binlogPath, string repoRoot)
    {
        var records = new List<ManagedCompilationRecord>();
        var build = BinaryLog.ReadBuild(binlogPath);

        build.VisitAllChildren<MSBuildTask>(task =>
        {
            if (!string.Equals(task.Name, "Csc", StringComparison.OrdinalIgnoreCase))
                return;

            var record = ExtractFromCscTask(task, repoRoot);
            if (record is not null)
                records.Add(record);
        });

        return records;
    }

    private static ManagedCompilationRecord? ExtractFromCscTask(MSBuildTask task, string repoRoot)
    {
        var sourceFiles = new SortedSet<string>(StringComparer.Ordinal);
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var noWarn = new SortedSet<string>(StringComparer.Ordinal);
        var analyzers = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        string targetType = "library";
        string langVersion = "";
        string? assemblyName = null;

        // Extract parameters from task children
        var folder = task.FindChild<Folder>("Parameters");
        if (folder is null)
            return null;

        foreach (var child in folder.Children)
        {
            if (child is Property prop)
            {
                switch (prop.Name)
                {
                    case "OutputAssembly":
                        assemblyName = Path.GetFileNameWithoutExtension(prop.Value);
                        break;
                    case "DefineConstants":
                        foreach (var d in prop.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                            defines.Add(d.Trim());
                        break;
                    case "NoWarn":
                        foreach (var w in prop.Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                            noWarn.Add(w.Trim());
                        break;
                    case "TargetType":
                        targetType = prop.Value;
                        break;
                    case "LangVersion":
                        langVersion = prop.Value;
                        break;
                    case "Nullable":
                    case "Unsafe":
                    case "CheckForOverflowUnderflow":
                    case "Deterministic":
                    case "HighEntropyVA":
                    case "Optimize":
                    case "AllowUnsafeBlocks":
                        flags.Add($"/{prop.Name.ToLowerInvariant()}:{prop.Value}");
                        break;
                }
            }
            else if (child is Parameter param)
            {
                switch (param.Name)
                {
                    case "Sources":
                        foreach (var item in param.Children.OfType<Item>())
                            sourceFiles.Add(NormalizePath(item.Text, repoRoot));
                        break;
                    case "References" or "ReferencePath":
                        foreach (var item in param.Children.OfType<Item>())
                            references.Add(Path.GetFileNameWithoutExtension(item.Text));
                        break;
                    case "Analyzers":
                        foreach (var item in param.Children.OfType<Item>())
                            analyzers.Add(Path.GetFileNameWithoutExtension(item.Text));
                        break;
                }
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
            Flags = flags,
            TargetType = targetType,
            LangVersion = langVersion,
            BuildSystem = "msbuild",
        };
    }

    private static string NormalizePath(string path, string repoRoot)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var normalized = Path.GetFullPath(path);
        var root = Path.GetFullPath(repoRoot).TrimEnd('/') + "/";
        if (normalized.StartsWith(root, StringComparison.Ordinal))
            return normalized[root.Length..];

        return normalized;
    }
}
