// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompileCommandComparer;

/// <summary>JSON-serializable target mapping configuration.</summary>
sealed class TargetMappingConfig
{
    [JsonPropertyName("native")]
    public List<NativeTargetMapping> Native { get; set; } = [];

    [JsonPropertyName("managed")]
    public List<ManagedTargetMapping> Managed { get; set; } = [];

    public static TargetMappingConfig? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TargetMappingConfig>(json);
    }
}

sealed class NativeTargetMapping
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("bazel_label")]
    public string BazelLabel { get; set; } = "";

    /// <summary>
    /// Regex or substring to match CMake output object paths against.
    /// CMake compile_commands.json groups files by the CMakeFiles/&lt;target&gt;.dir/ prefix
    /// in the -o argument.
    /// </summary>
    [JsonPropertyName("cmake_output_pattern")]
    public string CMakeOutputPattern { get; set; } = "";
}

sealed class ManagedTargetMapping
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("bazel_label")]
    public string BazelLabel { get; set; } = "";

    /// <summary>Repo-relative path to the MSBuild .csproj that produces this target.</summary>
    [JsonPropertyName("msbuild_project")]
    public string MsBuildProject { get; set; } = "";
}
