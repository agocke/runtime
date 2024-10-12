// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.DotNet.GenFacades;

var seeds = new List<string>();
string contractAssembly = "";
var compileFiles = new List<string>();
string outputSourcePath = "";
var omitTypes = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (StartsWith(arg, "--ref-path=", out var seed))
    {
        seeds.Add(seed);
    }
    else if (StartsWith(arg, "--contractAssembly=", out var asm))
    {
        contractAssembly = asm;
    }
    else if (StartsWith(arg, "--file=", out var file))
    {
        compileFiles.Add(file);
    }
    else if (StartsWith(arg, "--outputSourcePath=", out var outPath))
    {
        outputSourcePath = outPath;
    }
    else if (StartsWith(arg, "--omitType=", out var omit))
    {
        omitTypes.Add(omit);
    }
}

Microsoft.DotNet.Build.Tasks.ILog logger = new Logger();

_ = GenPartialFacadeSourceGenerator.Execute(
    seeds: seeds.ToArray(),
    contractAssembly: contractAssembly,
    compileFiles: compileFiles.ToArray(),
    defineConstants: "",
    langVersion: "preview",
    outputSourcePath: outputSourcePath,
    logger: logger,
    OmitTypes: omitTypes.ToArray()
);

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
