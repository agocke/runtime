// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildEquivalenceCheck;

/// <summary>
/// Serializes and deserializes MSBuild managed compilation records to/from a
/// compact JSON file. This avoids re-parsing large .binlog files for repeated
/// equivalence checks.
/// </summary>
public static class MsbuildJsonStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(List<ManagedCompilationRecord> records, string path)
    {
        var dtos = records.Select(r => new RecordDto
        {
            AssemblyName = r.AssemblyName,
            SourceFiles = [.. r.SourceFiles],
            Defines = [.. r.Defines],
            References = [.. r.References],
            NoWarn = [.. r.NoWarn],
            Analyzers = r.Analyzers.Count > 0 ? [.. r.Analyzers] : null,
            Flags = r.Flags.Count > 0 ? [.. r.Flags] : null,
            TargetType = r.TargetType != "library" ? r.TargetType : null,
            LangVersion = !string.IsNullOrEmpty(r.LangVersion) ? r.LangVersion : null,
            OutputPath = !string.IsNullOrEmpty(r.OutputPath) ? r.OutputPath : null,
            IsReferenceAssembly = r.IsReferenceAssembly ? true : null,
        }).ToList();

        var json = JsonSerializer.Serialize(dtos, s_options);
        File.WriteAllText(path, json);
    }

    public static List<ManagedCompilationRecord> Load(string path)
    {
        var json = File.ReadAllText(path);
        var dtos = JsonSerializer.Deserialize<List<RecordDto>>(json, s_readOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize MSBuild JSON from {path}");

        return dtos.Select(d => new ManagedCompilationRecord
        {
            AssemblyName = d.AssemblyName,
            SourceFiles = new SortedSet<string>(d.SourceFiles ?? [], StringComparer.Ordinal),
            SourceFileOriginalPaths = [],
            Defines = new SortedSet<string>(d.Defines ?? [], StringComparer.Ordinal),
            References = new SortedSet<string>(d.References ?? [], StringComparer.Ordinal),
            NoWarn = new SortedSet<string>(d.NoWarn ?? [], StringComparer.Ordinal),
            Analyzers = new SortedSet<string>(d.Analyzers ?? [], StringComparer.Ordinal),
            Flags = new SortedSet<string>(d.Flags ?? [], StringComparer.Ordinal),
            TargetType = d.TargetType ?? "library",
            LangVersion = d.LangVersion ?? "",
            BuildSystem = "msbuild",
            OutputPath = d.OutputPath ?? "",
            IsReferenceAssembly = d.IsReferenceAssembly ?? false,
        }).ToList();
    }

    private sealed class RecordDto
    {
        public required string AssemblyName { get; set; }
        public List<string>? SourceFiles { get; set; }
        public List<string>? Defines { get; set; }
        public List<string>? References { get; set; }
        public List<string>? NoWarn { get; set; }
        public List<string>? Analyzers { get; set; }
        public List<string>? Flags { get; set; }
        public string? TargetType { get; set; }
        public string? LangVersion { get; set; }
        public string? OutputPath { get; set; }
        public bool? IsReferenceAssembly { get; set; }
    }
}
