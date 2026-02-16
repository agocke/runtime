// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace CompileCommandComparer;

/// <summary>
/// Parses CMake-generated compile_commands.json into CompilationTargets,
/// grouped by the CMake target name extracted from the -o path.
/// </summary>
static class CMakeParser
{
    public static Dictionary<string, CompilationTarget> Parse(string path, string repoRoot)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Warning: compile_commands.json not found at '{path}'");
            return new Dictionary<string, CompilationTarget>();
        }

        string json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<CompileCommandEntry>>(json) ?? [];

        // Group entries by target name (extracted from -o CMakeFiles/<target>.dir/...)
        var groups = new Dictionary<string, List<CompileCommandEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string targetName = ExtractTargetName(entry);
            if (!groups.TryGetValue(targetName, out var list))
            {
                list = [];
                groups[targetName] = list;
            }
            list.Add(entry);
        }

        var targets = new Dictionary<string, CompilationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var (targetName, entryList) in groups)
        {
            var target = BuildTarget(targetName, entryList, repoRoot);
            targets[targetName] = target;
        }

        return targets;
    }

    static CompilationTarget BuildTarget(string name, List<CompileCommandEntry> entries, string repoRoot)
    {
        var allDefines = new SortedSet<string>(StringComparer.Ordinal);
        var allIncludes = new SortedSet<string>(StringComparer.Ordinal);
        var allFlags = new SortedSet<string>(StringComparer.Ordinal);
        var sources = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            // Add source file
            string srcFile = Normalizer.NormalizePath(
                Path.IsPathRooted(entry.File)
                    ? entry.File
                    : Path.Combine(entry.Directory, entry.File),
                repoRoot);
            sources.Add(srcFile);

            // Parse the command into args
            var args = SplitCommand(entry.Command ?? entry.Arguments);
            var (defines, includes, flags) = Normalizer.ClassifyArgs(args, repoRoot);
            allDefines.UnionWith(defines);
            allIncludes.UnionWith(includes);
            allFlags.UnionWith(flags);
        }

        return new CompilationTarget
        {
            Name = name,
            BuildSystem = "CMake",
            SourceFiles = sources,
            Defines = allDefines,
            IncludePaths = allIncludes,
            CompilerFlags = allFlags,
        };
    }

    static string ExtractTargetName(CompileCommandEntry entry)
    {
        // Look for -o path containing CMakeFiles/<target>.dir/
        var args = SplitCommand(entry.Command ?? entry.Arguments);
        string cmd = string.Join(' ', args);
        const string marker = "CMakeFiles/";
        int idx = cmd.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int start = idx + marker.Length;
            int end = cmd.IndexOf(".dir/", start, StringComparison.Ordinal);
            if (end > start)
                return cmd[start..end];
        }

        // Fallback: try to find target from -o argument
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-o")
            {
                string output = args[i + 1];
                int cmakeIdx = output.IndexOf(marker, StringComparison.Ordinal);
                if (cmakeIdx >= 0)
                {
                    int s = cmakeIdx + marker.Length;
                    int e = output.IndexOf(".dir/", s, StringComparison.Ordinal);
                    if (e > s)
                        return output[s..e];
                }
            }
        }

        // Last fallback: use directory name
        return Path.GetFileName(entry.Directory);
    }

    static List<string> SplitCommand(JsonElement? commandOrArgs)
    {
        if (commandOrArgs is JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Array)
                return [.. elem.EnumerateArray().Select(e => e.GetString() ?? "")];
            if (elem.ValueKind == JsonValueKind.String)
                return ShellSplit(elem.GetString() ?? "");
        }

        return [];
    }

    /// <summary>Simple shell-like argument splitting.</summary>
    static List<string> ShellSplit(string command)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;
        char quoteChar = '\0';

        foreach (char c in command)
        {
            if (inQuote)
            {
                if (c == quoteChar)
                    inQuote = false;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == ' ')
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            args.Add(current.ToString());

        return args;
    }

    sealed class CompileCommandEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("directory")]
        public string Directory { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("file")]
        public string File { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("command")]
        public JsonElement? Command { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("arguments")]
        public JsonElement? Arguments { get; set; }
    }
}
