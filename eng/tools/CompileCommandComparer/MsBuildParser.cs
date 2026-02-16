// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Logging.StructuredLogger;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace CompileCommandComparer;

/// <summary>
/// Parses an MSBuild .binlog to extract Csc task invocations
/// and produce CompilationTargets grouped by project path.
/// </summary>
static class MsBuildParser
{
    public static Dictionary<string, CompilationTarget> Parse(string binlogPath, string repoRoot)
    {
        if (!File.Exists(binlogPath))
        {
            Console.Error.WriteLine($"Warning: binlog not found at '{binlogPath}'");
            return new Dictionary<string, CompilationTarget>();
        }

        var build = BinaryLog.ReadBuild(binlogPath);
        var targets = new Dictionary<string, CompilationTarget>(StringComparer.OrdinalIgnoreCase);

        VisitNode(build, targets, repoRoot);

        return targets;
    }

    static void VisitNode(BaseNode node, Dictionary<string, CompilationTarget> targets, string repoRoot)
    {
        if (node is Task task && task.Name == "Csc")
        {
            ProcessCscTask(task, targets, repoRoot);
        }

        if (node is TreeNode treeNode)
        {
            foreach (var child in treeNode.Children)
            {
                if (child is BaseNode childNode)
                    VisitNode(childNode, targets, repoRoot);
            }
        }
    }

    static void ProcessCscTask(Task cscTask, Dictionary<string, CompilationTarget> targets, string repoRoot)
    {
        // Walk up to find the owning project
        string projectPath = FindProjectPath(cscTask) ?? "unknown";
        string repoRelativeProject = Normalizer.NormalizePath(projectPath, repoRoot);

        var sources = new SortedSet<string>(StringComparer.Ordinal);
        var defines = new SortedSet<string>(StringComparer.Ordinal);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);

        // Extract parameters from the task
        if (cscTask is TreeNode taskTree)
        {
            foreach (var child in taskTree.Children)
            {
                if (child is Property prop)
                {
                    ProcessCscProperty(prop, sources, defines, references, flags, repoRoot);
                }
                else if (child is Folder folder)
                {
                    foreach (var folderChild in folder.Children)
                    {
                        if (folderChild is Property fp)
                            ProcessCscProperty(fp, sources, defines, references, flags, repoRoot);
                        else if (folderChild is Item item)
                            ProcessCscItem(folder.Name, item, sources, references, repoRoot);
                    }
                }
            }
        }

        // Extract from command line if available
        string? commandLine = ExtractCommandLine(cscTask);
        if (commandLine is not null)
        {
            var args = ParseCscCommandLine(commandLine);
            var (cscDefines, cscRefs, cscFlags) = Normalizer.ClassifyCscArgs(args, repoRoot);
            defines.UnionWith(cscDefines);
            references.UnionWith(cscRefs);
            flags.UnionWith(cscFlags);
        }

        if (targets.TryGetValue(repoRelativeProject, out var existing))
        {
            existing.SourceFiles.UnionWith(sources);
            existing.Defines.UnionWith(defines);
            existing.References.UnionWith(references);
            existing.CompilerFlags.UnionWith(flags);
        }
        else
        {
            targets[repoRelativeProject] = new CompilationTarget
            {
                Name = repoRelativeProject,
                BuildSystem = "MSBuild",
                SourceFiles = sources,
                Defines = defines,
                References = references,
                CompilerFlags = flags,
            };
        }
    }

    static void ProcessCscProperty(
        Property prop,
        SortedSet<string> sources,
        SortedSet<string> defines,
        SortedSet<string> references,
        SortedSet<string> flags,
        string repoRoot)
    {
        switch (prop.Name)
        {
            case "DefineConstants":
                foreach (string d in prop.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    defines.Add(d.Trim());
                break;
            case "Sources":
                foreach (string s in prop.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    sources.Add(Normalizer.NormalizePath(s.Trim(), repoRoot));
                break;
        }
    }

    static void ProcessCscItem(
        string folderName,
        Item item,
        SortedSet<string> sources,
        SortedSet<string> references,
        string repoRoot)
    {
        switch (folderName)
        {
            case "Sources":
                sources.Add(Normalizer.NormalizePath(item.Text, repoRoot));
                break;
            case "References":
            case "ReferencePath":
                references.Add(Path.GetFileNameWithoutExtension(item.Text));
                break;
        }
    }

    static string? FindProjectPath(BaseNode node)
    {
        BaseNode? current = node;
        while (current is not null)
        {
            if (current is Project p)
                return p.ProjectFile;
            current = current.Parent as BaseNode;
        }

        return null;
    }

    static string? ExtractCommandLine(Task task)
    {
        if (task is TreeNode tree)
        {
            foreach (var child in tree.Children)
            {
                if (child is Property { Name: "CommandLineArguments" } prop)
                    return prop.Value;
            }
        }

        return null;
    }

    static List<string> ParseCscCommandLine(string commandLine)
    {
        var args = new List<string>();
        foreach (string line in commandLine.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                args.Add(trimmed);
        }

        return args;
    }
}
