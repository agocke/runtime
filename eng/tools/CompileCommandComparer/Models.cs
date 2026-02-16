// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CompileCommandComparer;

/// <summary>
/// Represents the compilation settings for a single build target,
/// aggregated from all compilation actions that contribute to it.
/// </summary>
sealed class CompilationTarget
{
    public required string Name { get; init; }
    public required string BuildSystem { get; init; }
    public SortedSet<string> SourceFiles { get; init; } = [];
    public SortedSet<string> Defines { get; init; } = [];
    public SortedSet<string> IncludePaths { get; init; } = [];
    public SortedSet<string> CompilerFlags { get; init; } = [];
    public SortedSet<string> References { get; init; } = [];
}

/// <summary>Diff result for a single comparison dimension.</summary>
sealed class SetDiff
{
    public required string Dimension { get; init; }
    public SortedSet<string> OnlyInBaseline { get; init; } = [];
    public SortedSet<string> OnlyInBazel { get; init; } = [];
    public bool IsMatch => OnlyInBaseline.Count == 0 && OnlyInBazel.Count == 0;
}

/// <summary>Full comparison result for one mapped target pair.</summary>
sealed class TargetComparisonResult
{
    public required string TargetName { get; init; }
    public bool BaselineFound { get; init; }
    public bool BazelFound { get; init; }
    public List<SetDiff> Diffs { get; init; } = [];
    public bool IsMatch => BaselineFound && BazelFound && Diffs.All(d => d.IsMatch);
}
