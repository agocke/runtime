// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Microsoft.Build.Framework;

#nullable enable

namespace ILCompiler.Build.Tasks;

public class SwapRuntimePacks : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] ResolvedRuntimePacks { get; set; } = null!;

    [Required]
    public ITaskItem[] NativeAotRuntimePacks { get; set; } = null!;

    [Output]
    public ITaskItem[]? SwappedRuntimePacks { get; set; } = null;

    public override bool Execute()
    {
        // The native AOT runtime pack subs in for the netcoreapp runtime

        if (NativeAotRuntimePacks.Length == 0)
        {
            Log.LogMessage("No NativeAotRuntimePacks found");
            SwappedRuntimePacks = ResolvedRuntimePacks;
            return true;
        }

        var newRuntimePacks = new List<ITaskItem>();
        foreach (var pack in ResolvedRuntimePacks)
        {
            bool swapped = false;
            if (pack.GetMetadata("FrameworkName") == "Microsoft.NETCore.App")
            {
                var packRid = pack.GetMetadata("RuntimeIdentifier");
                var packVersion = pack.GetMetadata("NuGetPackageVersion");
                foreach (var nativeAotPack in NativeAotRuntimePacks)
                {
                    if (nativeAotPack.GetMetadata("NuGetPackageId")?.EndsWith(packRid) == true &&
                        nativeAotPack.GetMetadata("NuGetPackageVersion") == packVersion)
                    {
                        nativeAotPack.SetMetadata("FrameworkName", "Microsoft.NETCore.App");
                        newRuntimePacks.Add(nativeAotPack);
                        swapped = true;
                        break;
                    }
                }

                if (!swapped)
                {
                    Log.LogError($"Could not find a NativeAotRuntimePack to replace {pack.ItemSpec} ({pack.GetMetadata("RuntimeIdentifier")}, {pack.GetMetadata("NuGetPackageVersion")})");
                    return false;
                }
            }
        }

        SwappedRuntimePacks = newRuntimePacks.ToArray();

        return true;
    }
}
