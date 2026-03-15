// CopilotFixSync.cs
//
// Uses the GitHub Copilot SDK for .NET to automatically attempt BUILD.bazel
// fixes based on a sync detection report.
//
// Usage: dotnet run CopilotFixSync.cs -- <report-file-path>
//
// Requires:
//   - GitHub.Copilot.SDK NuGet package
//   - Copilot CLI installed and authenticated (COPILOT_GITHUB_TOKEN env var)

#:package GitHub.Copilot.SDK@*

using GitHub.Copilot.SDK;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run CopilotFixSync.cs -- <report-file-path>");
    return 1;
}

var reportPath = args[0];
if (!File.Exists(reportPath))
{
    Console.Error.WriteLine($"Report file not found: {reportPath}");
    return 1;
}

var detectionReport = File.ReadAllText(reportPath);

Console.WriteLine("Starting Copilot SDK session...");

await using var client = new CopilotClient();
await client.StartAsync();

await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = "claude-sonnet-4",
    OnPermissionRequest = PermissionHandler.ApproveAll
});

var prompt = $"""
    You are updating Bazel build files for the dotnet/runtime repository after
    merging upstream changes from release/10.0 into the bazel branch.

    ## Context: How Bazel build files work in this repository

    ### Source files in BUILD.bazel

    Libraries under src/libraries/ have BUILD.bazel files that define Bazel build
    targets. The key patterns are:

    1. **Explicit srcs lists**: Most libraries list source files explicitly:
       ```
       srcs = [
           "src/System/Collections/Generic/LinkedList.cs",
           "src/System/Collections/ThrowHelper.cs",
       ]
       ```

    2. **Glob patterns**: Some libraries use glob() which auto-includes files:
       ```
       srcs = glob(["src/System/**/*.cs"])
       ```
       Files in glob-based libraries are auto-included and need no changes.

    3. **Cross-package references**: Shared source files use Bazel labels:
       - `$(CoreLibSharedDir)path` in MSBuild → `"//src/libraries/System.Private.CoreLib:src/path"` in Bazel
       - `$(CommonPath)path` in MSBuild → `"//src/libraries/Common:src/path"` in Bazel

    4. **Dependencies**: `<ProjectReference>` in .csproj → `deps` list in BUILD.bazel:
       - `//src/libraries:ref_<AssemblyName>` for reference assemblies
       - `//src/libraries:impl_<AssemblyName>` for implementation assemblies

    5. **Defines**: `<DefineConstants>` in .csproj → `defines` list in BUILD.bazel

    ### NuGet package versions (CRITICAL)

    Some NuGet packages are referenced with **version-pinned labels** that embed
    the exact version string. These appear across BUILD.bazel, MODULE.bazel,
    paket/paket.main.bzl, and src/libraries/defs.bzl files. The pattern is:

        @nuget.<lowercased.package.name>.v<version>//:path/to/file.dll

    For example:
    - `@nuget.microsoft.dotnet.arcade.sdk.v10.0.0-beta.26102.102//:tools/snk/MSFT.snk`
    - `@nuget.microsoft.dotnet.genfacades.v10.0.0-beta.26102.102//:tools/net/Microsoft.DotNet.GenFacades.dll`
    - `@nuget.microsoft.dotnet.xunitconsolerunner.v2.9.3-beta.26102.102//:tools/net/xunit.console.dll`

    When upstream bumps a package version, ALL references to the old versioned
    label must be updated to the new version. This is a simple text replacement
    of the version string within these labels. The files that contain versioned
    NuGet labels are:

    - `MODULE.bazel` — `use_repo(...)` declarations
    - `paket/paket.main.bzl` — NuGet package declarations (name, version, sha512).
      **Important**: Only update the `"version"` field. Do NOT change the `"sha512"`
      field — it will be updated separately by tooling.
    - `eng/BUILD.bazel` — xunit console runner references
    - `src/libraries/defs.bzl` — default signing key references
    - `src/libraries/BUILD.bazel` — signing key references
    - `src/coreclr/System.Private.CoreLib/BUILD.bazel` — signing key references
    - `src/tools/bazel/GenFacades/BUILD.bazel` — GenFacades tool DLL references
    - `src/tools/bazel/GenNotSupportedSource/BUILD.bazel` — GenFacades tool DLL references
    - `src/tools/bazel/GenerateResxSource/BUILD.bazel` — Arcade SDK tool references

    **Example**: If `eng/Version.Details.props` bumps `MicrosoftDotNetArcadeSdkPackageVersion`
    from `10.0.0-beta.26102.102` to `10.0.0-beta.26110.124`, then every occurrence of
    `nuget.microsoft.dotnet.arcade.sdk.v10.0.0-beta.26102.102` must be replaced with
    `nuget.microsoft.dotnet.arcade.sdk.v10.0.0-beta.26110.124` across all the files above.

    Note: not all packages use versioned labels. Most test dependencies use
    unversioned `@paket.main//package.name` references which resolve automatically
    and don't need updating.

    ## Detection Report

    {detectionReport}

    ## Instructions

    Based on the detection report above, make the necessary changes to Bazel build
    files. For each item flagged as needing an update:

    - **Version bump affects Bazel NuGet deps**: This is the most common case.
      Find every occurrence of the old versioned label and replace the version
      string with the new one. Check all `.bazel` and `.bzl` files listed above.
      The old and new versions can be read from the diff in the detection report.

    - **New .cs files (explicit srcs)**: Add the file path to the `srcs` list in
      the appropriate BUILD.bazel file. Maintain alphabetical ordering.

    - **Removed .cs files (explicit srcs)**: Remove the file path from the `srcs`
      list in the appropriate BUILD.bazel file.

    - **New ProjectReferences in .csproj**: Add the corresponding
      `//src/libraries:ref_<AssemblyName>` entry to the `deps` list.

    - **New DefineConstants in .csproj**: Add the constant to the `defines` list.

    - **New .resx files**: Add a `resx_file` parameter if not already present.

    You may modify: BUILD.bazel, MODULE.bazel, paket/paket.main.bzl, and
    src/libraries/defs.bzl files. Do not modify any other file types.
    """;

try
{
    var response = await session.SendAndWaitAsync(
        new MessageOptions { Prompt = prompt },
        TimeSpan.FromMinutes(20));

    Console.WriteLine(response?.Data?.Content ?? "(no response)");
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Copilot session timed out after 20 minutes");
    return 1;
}
catch (TimeoutException)
{
    Console.Error.WriteLine("Copilot session timed out after 20 minutes");
    return 1;
}

await client.StopAsync();

Console.WriteLine("Copilot session completed.");
return 0;
