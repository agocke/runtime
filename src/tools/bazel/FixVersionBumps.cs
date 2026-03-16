// FixVersionBumps.cs
//
// Deterministic script that updates Bazel NuGet version-pinned labels
// when eng/Version.Details.props bumps package versions.
//
// Usage: dotnet run FixVersionBumps.cs -- <base-ref> <head-ref> [--repo-root <path>]
//
// Reads the git diff of eng/Version.Details.props between base-ref and head-ref,
// extracts old→new version mappings, cross-references against paket/paket.main.bzl,
// and performs text replacement across all Bazel files that use versioned labels.

using System.Diagnostics;
using System.Text.RegularExpressions;

// ─── Parse arguments ──────────────────────────────────────────────────────────

string? baseRef = null;
string? headRef = null;
string repoRoot = ".";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repo-root" when i + 1 < args.Length:
            repoRoot = args[++i];
            break;
        default:
            if (baseRef is null) baseRef = args[i];
            else if (headRef is null) headRef = args[i];
            else
            {
                Console.Error.WriteLine($"Unexpected argument: {args[i]}");
                return 2;
            }
            break;
    }
}

if (baseRef is null || headRef is null)
{
    Console.Error.WriteLine("Usage: dotnet run FixVersionBumps.cs -- <base-ref> <head-ref> [--repo-root <path>]");
    return 2;
}

// ─── Step 1: Get version bump diff ────────────────────────────────────────────

var diffOutput = RunGit($"diff {baseRef}..{headRef} -- eng/Version.Details.props eng/Versions.props", repoRoot);
if (string.IsNullOrWhiteSpace(diffOutput))
{
    Console.WriteLine("No version file changes detected.");
    return 0;
}

// Parse diff lines to extract old→new version pairs per package.
// Lines look like:
//   -    <MicrosoftDotNetArcadeSdkPackageVersion>10.0.0-beta.26102.102</...>
//   +    <MicrosoftDotNetArcadeSdkPackageVersion>10.0.0-beta.26110.124</...>

var versionPropRegex = new Regex(@"^([+-])\s*<(\w+)PackageVersion>([^<]+)</\w+PackageVersion>");

// Group by property name, collecting removed (-) and added (+) versions
var versionChanges = new Dictionary<string, (string? OldVersion, string? NewVersion)>();

foreach (var line in diffOutput.Split('\n'))
{
    var match = versionPropRegex.Match(line);
    if (!match.Success)
        continue;

    var sign = match.Groups[1].Value;
    var propName = match.Groups[2].Value;    // e.g., "MicrosoftDotNetArcadeSdk"
    var version = match.Groups[3].Value;     // e.g., "10.0.0-beta.26102.102"

    if (!versionChanges.TryGetValue(propName, out var entry))
        entry = (null, null);

    if (sign == "-")
        entry = (version, entry.NewVersion);
    else
        entry = (entry.OldVersion, version);

    versionChanges[propName] = entry;
}

// Filter to entries that have both old and new, and they differ
var bumps = versionChanges
    .Where(kv => kv.Value.OldVersion is not null
              && kv.Value.NewVersion is not null
              && kv.Value.OldVersion != kv.Value.NewVersion)
    .ToDictionary(kv => kv.Key, kv => (Old: kv.Value.OldVersion!, New: kv.Value.NewVersion!));

if (bumps.Count == 0)
{
    Console.WriteLine("No version bumps found in diff.");
    return 0;
}

Console.WriteLine($"Found {bumps.Count} version bump(s) in eng/Version.Details.props:");
foreach (var (prop, versions) in bumps)
    Console.WriteLine($"  {prop}: {versions.Old} → {versions.New}");

// ─── Step 2: Cross-reference against paket/paket.main.bzl ────────────────────

var paketPath = Path.Combine(repoRoot, "paket/paket.main.bzl");
if (!File.Exists(paketPath))
{
    Console.Error.WriteLine($"paket/paket.main.bzl not found at {paketPath}");
    return 1;
}

var paketContent = File.ReadAllText(paketPath);

// Extract package entries: {"name": "Package.Name", ... "version": "X.Y.Z", ...}
var paketNameRegex = new Regex(@"""name"":\s*""([^""]+)""");
var paketPackageNames = paketNameRegex.Matches(paketContent)
    .Select(m => m.Groups[1].Value)
    .ToHashSet();

// Build lookup: normalized MSBuild prop name → NuGet package name
// e.g., "microsoftdotnetarcadesdk" → "Microsoft.DotNet.Arcade.Sdk"
var normalizedToPaketName = new Dictionary<string, string>();
foreach (var name in paketPackageNames)
{
    var normalized = name.Replace(".", "").ToLowerInvariant();
    normalizedToPaketName[normalized] = name;
}

// Match bumped MSBuild properties to paket packages
var replacements = new List<(string PackageName, string OldVersion, string NewVersion)>();

foreach (var (propName, versions) in bumps)
{
    var normalizedProp = propName.ToLowerInvariant();
    if (normalizedToPaketName.TryGetValue(normalizedProp, out var paketName))
    {
        replacements.Add((paketName, versions.Old, versions.New));
    }
}

if (replacements.Count == 0)
{
    Console.WriteLine("No bumped packages are used in Bazel NuGet deps.");
    return 0;
}

Console.WriteLine($"\n{replacements.Count} package(s) affect Bazel:");
foreach (var r in replacements)
    Console.WriteLine($"  {r.PackageName}: {r.OldVersion} → {r.NewVersion}");

// ─── Step 3: Perform replacements ─────────────────────────────────────────────

// Files that contain version-pinned NuGet labels
string[] bazelFiles = [
    "MODULE.bazel",
    "eng/BUILD.bazel",
    "src/libraries/defs.bzl",
    "src/libraries/BUILD.bazel",
    "src/coreclr/System.Private.CoreLib/BUILD.bazel",
    "src/tools/bazel/GenFacades/BUILD.bazel",
    "src/tools/bazel/GenNotSupportedSource/BUILD.bazel",
    "src/tools/bazel/GenerateResxSource/BUILD.bazel",
];

int totalReplacements = 0;

foreach (var (packageName, oldVersion, newVersion) in replacements)
{
    var lowerName = packageName.ToLowerInvariant();

    // Replace versioned labels: nuget.<pkg>.v<old> → nuget.<pkg>.v<new>
    var oldLabel = $"nuget.{lowerName}.v{oldVersion}";
    var newLabel = $"nuget.{lowerName}.v{newVersion}";

    foreach (var relPath in bazelFiles)
    {
        var fullPath = Path.Combine(repoRoot, relPath);
        if (!File.Exists(fullPath))
            continue;

        var content = File.ReadAllText(fullPath);
        if (!content.Contains(oldLabel))
            continue;

        var count = CountOccurrences(content, oldLabel);
        var updated = content.Replace(oldLabel, newLabel);
        File.WriteAllText(fullPath, updated);
        totalReplacements += count;
        Console.WriteLine($"  {relPath}: {count} replacement(s) ({oldLabel})");
    }

    // Update paket/paket.main.bzl: replace the "version" field only.
    // Pattern: "name": "Package.Name", "id": "...", "version": "<old>"
    // We target the specific package entry to avoid false matches on sha512.
    var paketUpdated = paketContent;
    var paketVersionPattern = $@"(""name"":\s*""{Regex.Escape(packageName)}""[^}}]*?""version"":\s*""){Regex.Escape(oldVersion)}("")";
    var paketReplacement = $"${{1}}{newVersion}${{2}}";

    var paketRegex = new Regex(paketVersionPattern, RegexOptions.Singleline);
    if (paketRegex.IsMatch(paketUpdated))
    {
        paketUpdated = paketRegex.Replace(paketUpdated, paketReplacement);
        totalReplacements++;
        Console.WriteLine($"  paket/paket.main.bzl: updated version for {packageName}");
    }

    paketContent = paketUpdated;
}

// Write paket file once after all replacements
File.WriteAllText(paketPath, paketContent);

Console.WriteLine($"\nDone. {totalReplacements} total replacement(s) across all files.");
return 0;

// ─── Helpers ──────────────────────────────────────────────────────────────────

static string RunGit(string arguments, string workingDir)
{
    var psi = new ProcessStartInfo("git", arguments)
    {
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var proc = Process.Start(psi)!;
    var output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    return output;
}

static int CountOccurrences(string text, string pattern)
{
    int count = 0;
    int idx = 0;
    while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
    {
        count++;
        idx += pattern.Length;
    }
    return count;
}
