// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var resxGen = new Microsoft.DotNet.Arcade.GenerateResxSource()
{
    Language = "C#",
    OutputPath = "",
    ResourceName = "",
    ResourceFile = "",
};
resxGen.Execute();
Console.WriteLine("Hello, World!");
