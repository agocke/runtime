// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace CompileCommandComparer;

/// <summary>
/// Matches parsed targets between build systems using the mapping configuration.
/// </summary>
static class Matcher
{
    /// <summary>
    /// Find a CMake target whose name matches the given pattern (substring or regex).
    /// </summary>
    public static CompilationTarget? FindCMakeTarget(
        Dictionary<string, CompilationTarget> targets, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        // Try exact match first
        if (targets.TryGetValue(pattern, out var exact))
            return exact;

        // Try substring match
        foreach (var (key, target) in targets)
        {
            if (key.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return target;
        }

        // Try regex
        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            foreach (var (key, target) in targets)
            {
                if (regex.IsMatch(key))
                    return target;
            }
        }
        catch (RegexParseException)
        {
            // Not a valid regex, ignore
        }

        return null;
    }

    /// <summary>
    /// Find a Bazel target by label (exact match or suffix match).
    /// </summary>
    public static CompilationTarget? FindBazelTarget(
        Dictionary<string, CompilationTarget> targets, string label)
    {
        if (string.IsNullOrEmpty(label))
            return null;

        // Exact match
        if (targets.TryGetValue(label, out var exact))
            return exact;

        // Try without leading //
        string normalized = label.TrimStart('/');
        foreach (var (key, target) in targets)
        {
            if (key.TrimStart('/') == normalized)
                return target;
        }

        return null;
    }

    /// <summary>
    /// Find an MSBuild target by project path (normalized to repo-relative).
    /// </summary>
    public static CompilationTarget? FindMsBuildTarget(
        Dictionary<string, CompilationTarget> targets, string projectPath, string repoRoot)
    {
        if (string.IsNullOrEmpty(projectPath))
            return null;

        string normalized = Normalizer.NormalizePath(projectPath, repoRoot);

        // Exact match on normalized path
        if (targets.TryGetValue(normalized, out var exact))
            return exact;

        // Suffix match (project path may be partially qualified)
        foreach (var (key, target) in targets)
        {
            if (key.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(key, StringComparison.OrdinalIgnoreCase))
                return target;
        }

        return null;
    }
}
