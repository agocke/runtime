// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;

string? outputPath = null;
string? resourceName = null;
string? resourceFile = null;

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i].TrimStart('\'').TrimEnd('\'');
    if (StartsWith(arg, "--output-path=", out var outPath))
    {
        outputPath = outPath;
    }
    else if (StartsWith(arg, "--resource-name=", out var resName))
    {
        resourceName = resName;
    }
    else if (StartsWith(arg, "--resource-file=", out var resFile))
    {
        resourceFile = resFile;
    }

}

var resxGen = new Microsoft.DotNet.Arcade.GenerateResxSource()
{
    Language = "C#",
    OutputPath = outputPath ?? throw new Exception("No output path specified"),
    ResourceName = resourceName ?? throw new Exception("No resource name specified"),
    ResourceFile = resourceFile ?? throw new Exception("No resource file specified"),
    ResourceClassName = "System.SR",
    OmitGetResourceString = true,
    IncludeDefaultValues = true,
};
return resxGen.Execute() ? 0 : 1;

static bool StartsWith(string s, string prefix, [NotNullWhen(true)] out string? rest)
{
    if (s.StartsWith(prefix))
    {
        rest = s[prefix.Length..];
        return true;
    }
    rest = null;
    return false;
}
