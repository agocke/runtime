// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Tasks;

Console.WriteLine("Hello, World!");

var srcPath = "/home/andy/code/runtime/src/libraries/System.Console/src/Resources/Strings.resx";
string[] _references = [
    "/home/andy/code/runtime/artifacts/bin/microsoft.netcore.app.ref/ref/net9.0/Microsoft.Win32.Primitives.dll",
    "/home/andy/code/runtime/artifacts/bin/microsoft.netcore.app.ref/ref/net9.0/System.Collections.dll",
    "/home/andy/code/runtime/artifacts/bin/microsoft.netcore.app.ref/ref/net9.0/System.Memory.dll",
    "/home/andy/code/runtime/artifacts/bin/microsoft.netcore.app.ref/ref/net9.0/System.Runtime.dll",
    "/home/andy/code/runtime/artifacts/bin/microsoft.netcore.app.ref/ref/net9.0/System.Runtime.InteropServices.dll",
    "/home/andy/code/runtime/artifacts/bin/microsoft.netcore.app.ref/ref/net9.0/System.Text.Encoding.Extensions.dll",
    "/home/andy/code/runtime/artifacts/bin/microsoft.netcore.app.ref/ref/net9.0/System.Threading.dll",
];
var outPath = "/home/andy/code/runtime/artifacts/obj/System.Console/Debug/net9.0-unix/FxResources.System.Console.SR.resources";


var logger = new Logger();
var process = new ProcessResourceFiles(
    logger,
    srcPath,
    outPath,
    _references);
process.Run();

return logger.HasLoggedErrors ? 1 : 0;
