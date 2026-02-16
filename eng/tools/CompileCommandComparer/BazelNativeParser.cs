// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace CompileCommandComparer;

/// <summary>
/// Parses Bazel aquery --output=jsonproto output for CppCompile actions.
/// Groups actions by target label to produce CompilationTargets.
/// </summary>
static class BazelNativeParser
{
    static string GetId(JsonElement elem, string propName)
    {
        var prop = elem.GetProperty(propName);
        return prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64().ToString()
            : prop.GetString() ?? "";
    }

    static string TryGetId(JsonElement elem, string propName)
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

        // Build lookup: artifact id -> path
        var artifactPaths = new Dictionary<string, string>();
        if (root.TryGetProperty("artifacts", out var artifacts))
        {
            foreach (var artifact in artifacts.EnumerateArray())
            {
                string id = GetId(artifact, "id");
                string fragPath = artifact.TryGetProperty("execPath", out var ep)
                    ? ep.GetString() ?? ""
                    : artifact.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(fragPath) && artifact.TryGetProperty("pathFragment", out _))
                {
                    fragPath = ResolvePathFragment(root, artifact);
                }
                if (id.Length > 0)
                    artifactPaths[id] = fragPath;
            }
        }

        // Build lookup: depset id -> list of artifact ids (flattened)
        var depSets = new Dictionary<string, List<string>>();
        if (root.TryGetProperty("depSetOfFiles", out var depSetsElem))
        {
            foreach (var ds in depSetsElem.EnumerateArray())
            {
                string id = GetId(ds, "id");
                var fileIds = new List<string>();
                if (ds.TryGetProperty("directArtifactIds", out var directIds))
                {
                    foreach (var fid in directIds.EnumerateArray())
                        fileIds.Add(fid.ValueKind == JsonValueKind.Number ? fid.GetInt64().ToString() : fid.GetString() ?? "");
                }
                depSets[id] = fileIds;
            }
        }

        // Build lookup: target id -> label
        var targetLabels = new Dictionary<string, string>();
        if (root.TryGetProperty("targets", out var targets))
        {
            foreach (var t in targets.EnumerateArray())
            {
                string id = GetId(t, "id");
                string label = t.GetProperty("label").GetString() ?? "";
                targetLabels[id] = label;
            }
        }

        // Parse actions
        var groups = new Dictionary<string, List<ActionInfo>>(StringComparer.Ordinal);
        if (root.TryGetProperty("actions", out var actions))
        {
            foreach (var action in actions.EnumerateArray())
            {
                string mnemonic = action.TryGetProperty("mnemonic", out var m)
                    ? m.GetString() ?? "" : "";

                if (mnemonic is not ("CppCompile" or "CCompile"))
                    continue;

                string targetId = TryGetId(action, "targetId");
                string label = targetLabels.GetValueOrDefault(targetId, targetId);

                var arguments = new List<string>();
                if (action.TryGetProperty("arguments", out var args))
                {
                    foreach (var arg in args.EnumerateArray())
                        arguments.Add(arg.GetString() ?? "");
                }

                // Collect input file paths
                var inputFiles = new List<string>();
                if (action.TryGetProperty("inputDepSetIds", out var inputIds))
                {
                    foreach (var dsId in inputIds.EnumerateArray())
                    {
                        string dsIdStr = dsId.ValueKind == JsonValueKind.Number
                            ? dsId.GetInt64().ToString() : dsId.GetString() ?? "";
                        if (depSets.TryGetValue(dsIdStr, out var fileIds))
                        {
                            foreach (var fid in fileIds)
                            {
                                if (artifactPaths.TryGetValue(fid, out var p))
                                    inputFiles.Add(p);
                            }
                        }
                    }
                }

                // Extract primary source from arguments (last .c/.cpp/.cc arg)
                string? primarySource = null;
                for (int i = arguments.Count - 1; i >= 0; i--)
                {
                    if (arguments[i].EndsWith(".c", StringComparison.Ordinal) ||
                        arguments[i].EndsWith(".cpp", StringComparison.Ordinal) ||
                        arguments[i].EndsWith(".cc", StringComparison.Ordinal) ||
                        arguments[i].EndsWith(".S", StringComparison.Ordinal))
                    {
                        primarySource = arguments[i];
                        break;
                    }
                }

                if (!groups.TryGetValue(label, out var list))
                {
                    list = [];
                    groups[label] = list;
                }
                list.Add(new ActionInfo(arguments, primarySource, inputFiles));
            }
        }

        // Build CompilationTargets
        var result = new Dictionary<string, CompilationTarget>(StringComparer.Ordinal);
        foreach (var (label, actionList) in groups)
        {
            var allDefines = new SortedSet<string>(StringComparer.Ordinal);
            var allIncludes = new SortedSet<string>(StringComparer.Ordinal);
            var allFlags = new SortedSet<string>(StringComparer.Ordinal);
            var sources = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var ai in actionList)
            {
                if (ai.PrimarySource is not null)
                    sources.Add(Normalizer.NormalizePath(ai.PrimarySource, repoRoot));

                var (defines, includes, flags) = Normalizer.ClassifyArgs(ai.Arguments, repoRoot);
                allDefines.UnionWith(defines);
                allIncludes.UnionWith(includes);
                allFlags.UnionWith(flags);
            }

            result[label] = new CompilationTarget
            {
                Name = label,
                BuildSystem = "Bazel",
                SourceFiles = sources,
                Defines = allDefines,
                IncludePaths = allIncludes,
                CompilerFlags = allFlags,
            };
        }

        return result;
    }

    static string ResolvePathFragment(JsonElement root, JsonElement artifact)
    {
        // Newer aquery format uses pathFragments array with label+parentId
        // For simplicity, fall back to empty if not resolvable
        return "";
    }

    record ActionInfo(List<string> Arguments, string? PrimarySource, List<string> InputFiles);
}
