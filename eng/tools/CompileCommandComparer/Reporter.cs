// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompileCommandComparer;

/// <summary>
/// Outputs comparison results to console or JSON.
/// Returns appropriate exit code.
/// </summary>
static class Reporter
{
    const string Red = "\x1b[31m";
    const string Green = "\x1b[32m";
    const string Yellow = "\x1b[33m";
    const string Cyan = "\x1b[1;36m";
    const string Reset = "\x1b[0m";

    /// <returns>0 = all match, 1 = diffs found, 2 = missing data</returns>
    public static int Report(List<TargetComparisonResult> results, bool asJson)
    {
        if (asJson)
            return ReportJson(results);

        return ReportConsole(results);
    }

    static int ReportConsole(List<TargetComparisonResult> results)
    {
        int matched = 0;
        int mismatched = 0;
        int missing = 0;

        foreach (var result in results)
        {
            Console.Write($"{Cyan}[{result.TargetName}]{Reset} ");

            if (!result.BaselineFound)
            {
                Console.WriteLine($"{Yellow}SKIP{Reset} — baseline target not found");
                missing++;
                continue;
            }
            if (!result.BazelFound)
            {
                Console.WriteLine($"{Yellow}SKIP{Reset} — Bazel target not found");
                missing++;
                continue;
            }

            if (result.IsMatch)
            {
                Console.WriteLine($"{Green}MATCH{Reset}");
                matched++;
            }
            else
            {
                Console.WriteLine($"{Red}DIFF{Reset}");
                mismatched++;
                foreach (var diff in result.Diffs.Where(d => !d.IsMatch))
                {
                    Console.WriteLine($"  {diff.Dimension}:");
                    foreach (var item in diff.OnlyInBaseline)
                        Console.WriteLine($"    {Red}- {item}{Reset}  (baseline only)");
                    foreach (var item in diff.OnlyInBazel)
                        Console.WriteLine($"    {Green}+ {item}{Reset}  (Bazel only)");
                }
            }
            Console.WriteLine();
        }

        // Summary
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"  Targets compared: {results.Count}");
        Console.WriteLine($"  {Green}Matched:{Reset} {matched}");
        if (mismatched > 0)
            Console.WriteLine($"  {Red}Mismatched:{Reset} {mismatched}");
        if (missing > 0)
            Console.WriteLine($"  {Yellow}Skipped:{Reset} {missing}");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        if (mismatched > 0)
            return 1;
        if (missing > 0 && matched == 0)
            return 2;

        return 0;
    }

    static int ReportJson(List<TargetComparisonResult> results)
    {
        var output = results.Select(r => new
        {
            target = r.TargetName,
            baselineFound = r.BaselineFound,
            bazelFound = r.BazelFound,
            match = r.IsMatch,
            diffs = r.Diffs.Where(d => !d.IsMatch).Select(d => new
            {
                dimension = d.Dimension,
                onlyInBaseline = d.OnlyInBaseline.ToArray(),
                onlyInBazel = d.OnlyInBazel.ToArray(),
            }).ToArray(),
        });

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        Console.WriteLine(JsonSerializer.Serialize(output, options));

        return results.Any(r => !r.IsMatch) ? 1 : 0;
    }
}
