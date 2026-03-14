// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

#pragma warning disable CA2007 // Consider calling ConfigureAwait (not applicable to top-level scripts)

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
    Model = "claude-sonnet-4"
});

var completionSource = new TaskCompletionSource();

session.On(evt =>
{
    switch (evt)
    {
        case AssistantMessageEvent msg:
            Console.WriteLine(msg.Data.Content);
            break;
        case SessionErrorEvent err:
            Console.Error.WriteLine($"[Error]: {err.Data.Message}");
            completionSource.TrySetException(new InvalidOperationException(err.Data.Message));
            break;
        case SessionIdleEvent:
            completionSource.TrySetResult();
            break;
    }
});

var prompt = $"""
    You are updating Bazel BUILD files for the dotnet/runtime repository after
    merging upstream changes from release/10.0 into the bazel branch.

    ## Context: How BUILD.bazel files work in this repository

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

    ## Detection Report

    {detectionReport}

    ## Instructions

    Based on the detection report above, make the necessary changes to BUILD.bazel
    files. For each item flagged as needing a BUILD.bazel update:

    - **New .cs files (explicit srcs)**: Add the file path to the `srcs` list in
      the appropriate BUILD.bazel file. Use the relative path from the library root
      (e.g., "src/System/Foo.cs"). Maintain alphabetical ordering within the list.

    - **Removed .cs files (explicit srcs)**: Remove the file path from the `srcs`
      list in the appropriate BUILD.bazel file.

    - **New ProjectReferences in .csproj**: Add the corresponding
      `//src/libraries:ref_<AssemblyName>` entry to the `deps` list.

    - **New DefineConstants in .csproj**: Add the constant to the `defines` list.

    - **New .resx files**: Add a `resx_file` parameter if not already present.

    If no BUILD.bazel changes are needed (e.g., all changes are in glob-based
    libraries or are version-only bumps), make no changes and explain why.

    Only modify BUILD.bazel files. Do not modify any other files.
    """;

await session.SendAsync(prompt);

// Wait for the session to complete its work
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
try
{
    await completionSource.Task.WaitAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Copilot session timed out after 10 minutes");
    return 1;
}

await client.StopAsync();

Console.WriteLine("Copilot session completed.");
return 0;
