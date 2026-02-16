// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace CompileCommandComparer;

/// <summary>
/// Parses Bazel aquery output for C# compilation actions (from rules_dotnet).
/// </summary>
static class BazelCSharpParser
{
    // Known mnemonics used by rules_dotnet for C# compilation
    static readonly HashSet<string> s_csharpMnemonics = new(StringComparer.OrdinalIgnoreCase)
    {
        "CSharpCompile",
        "CoreCompile",
        "DotnetCompile",
    };

    static string GetId(JsonElement elem, string propName = "id")
    {
        if (!elem.TryGetProperty(propName, out var prop))
            return "";
        return prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64().ToString()
            : prop.GetString() ?? "";
    }

    public static Dictionary<string, CompilationTarget> Parse(string path, string repoRoot)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Warning: Bazel aquery output not found at '{path}'");
            return new Dictionary<string, CompilationTarget>();
        }

        string json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Build target id -> label lookup
        var targetLabels = new Dictionary<string, string>();
        if (root.TryGetProperty("targets", out var targets))
        {
            foreach (var t in targets.EnumerateArray())
            {
                string id = GetId(t);
                string label = t.GetProperty("label").GetString() ?? "";
                targetLabels[id] = label;
            }
        }

        var groups = new Dictionary<string, ActionData>(StringComparer.Ordinal);

        if (root.TryGetProperty("actions", out var actions))
        {
            foreach (var action in actions.EnumerateArray())
            {
                string mnemonic = action.TryGetProperty("mnemonic", out var m)
                    ? m.GetString() ?? "" : "";

                if (!s_csharpMnemonics.Contains(mnemonic))
                    continue;

                string targetId = GetId(action, "targetId");
                string label = targetLabels.GetValueOrDefault(targetId, targetId);

                var arguments = new List<string>();
                if (action.TryGetProperty("arguments", out var args))
                {
                    foreach (var arg in args.EnumerateArray())
                        arguments.Add(arg.GetString() ?? "");
                }

                if (!groups.TryGetValue(label, out var data))
                {
                    data = new ActionData();
                    groups[label] = data;
                }
                data.AllArguments.AddRange(arguments);
            }
        }

        var result = new Dictionary<string, CompilationTarget>(StringComparer.Ordinal);
        foreach (var (label, data) in groups)
        {
            var sources = new SortedSet<string>(StringComparer.Ordinal);
            var defines = new SortedSet<string>(StringComparer.Ordinal);
            var references = new SortedSet<string>(StringComparer.Ordinal);
            var flags = new SortedSet<string>(StringComparer.Ordinal);

            foreach (string arg in data.AllArguments)
            {
                if (arg.EndsWith(".cs", StringComparison.Ordinal))
                {
                    sources.Add(Normalizer.NormalizePath(arg, repoRoot));
                    continue;
                }

                if (arg.StartsWith("/define:", StringComparison.Ordinal) ||
                    arg.StartsWith("-define:", StringComparison.Ordinal))
                {
                    foreach (string d in arg[8..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                        defines.Add(d);
                    continue;
                }

                if (arg.StartsWith("/reference:", StringComparison.Ordinal) ||
                    arg.StartsWith("-reference:", StringComparison.Ordinal) ||
                    arg.StartsWith("/r:", StringComparison.Ordinal) ||
                    arg.StartsWith("-r:", StringComparison.Ordinal))
                {
                    string refPath = arg[(arg.IndexOf(':') + 1)..];
                    references.Add(Path.GetFileNameWithoutExtension(refPath));
                    continue;
                }

                if (arg.StartsWith("/out:", StringComparison.Ordinal) ||
                    arg.StartsWith("-out:", StringComparison.Ordinal))
                    continue;

                // Skip the compiler executable
                if (arg.Contains("csc") || arg.Contains("dotnet"))
                    continue;

                flags.Add(arg);
            }

            result[label] = new CompilationTarget
            {
                Name = label,
                BuildSystem = "Bazel",
                SourceFiles = sources,
                Defines = defines,
                References = references,
                CompilerFlags = flags,
            };
        }

        return result;
    }

    sealed class ActionData
    {
        public List<string> AllArguments { get; } = [];
    }
}
