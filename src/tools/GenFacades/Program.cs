// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.GenFacades;

var facadeTask = new GenPartialFacadeSource();
facadeTask.Execute();
facadeTask.BuildEngine = new MockEngine(Console.Out);
Console.WriteLine("Hello, World!");
