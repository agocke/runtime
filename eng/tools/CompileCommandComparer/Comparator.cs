// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CompileCommandComparer;

/// <summary>
/// Compares two CompilationTargets (baseline vs Bazel) and produces a diff result.
/// </summary>
static class Comparator
{
    public static TargetComparisonResult Compare(
        string targetName,
        CompilationTarget? baseline,
        CompilationTarget? bazel,
        string repoRoot)
    {
        if (baseline is null || bazel is null)
        {
            return new TargetComparisonResult
            {
                TargetName = targetName,
                BaselineFound = baseline is not null,
                BazelFound = bazel is not null,
            };
        }

        var diffs = new List<SetDiff>();

        diffs.Add(ComputeDiff("Source Files", baseline.SourceFiles, bazel.SourceFiles));
        diffs.Add(ComputeDiff("Defines", baseline.Defines, bazel.Defines));
        diffs.Add(ComputeDiff("Include Paths", baseline.IncludePaths, bazel.IncludePaths));
        diffs.Add(ComputeDiff("Compiler Flags", baseline.CompilerFlags, bazel.CompilerFlags));

        if (baseline.References.Count > 0 || bazel.References.Count > 0)
            diffs.Add(ComputeDiff("References", baseline.References, bazel.References));

        return new TargetComparisonResult
        {
            TargetName = targetName,
            BaselineFound = true,
            BazelFound = true,
            Diffs = diffs,
        };
    }

    static SetDiff ComputeDiff(string dimension, SortedSet<string> baselineSet, SortedSet<string> bazelSet)
    {
        var onlyInBaseline = new SortedSet<string>(baselineSet, StringComparer.Ordinal);
        onlyInBaseline.ExceptWith(bazelSet);

        var onlyInBazel = new SortedSet<string>(bazelSet, StringComparer.Ordinal);
        onlyInBazel.ExceptWith(baselineSet);

        return new SetDiff
        {
            Dimension = dimension,
            OnlyInBaseline = onlyInBaseline,
            OnlyInBazel = onlyInBazel,
        };
    }
}
