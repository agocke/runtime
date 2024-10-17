// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

internal sealed class Logger
{
    public void LogError(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }
    public void LogWarning(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }
    public void LogMessage(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }

    internal void LogMessageFromResources(string v, string inFile, string outFileOrDir) => throw new NotImplementedException();
    internal void LogErrorFromResources(string rsCode, params object[] _)
        => LogError(rsCode);
    internal void LogErrorWithCodeFromResources(string rsCode, params object [] _)
        => LogError(rsCode);
}
