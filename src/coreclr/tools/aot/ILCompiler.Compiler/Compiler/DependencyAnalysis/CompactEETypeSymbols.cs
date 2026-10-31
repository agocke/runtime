// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ILCompiler.DependencyAnalysis;

public partial class EETypeNode
{
    internal static Internal.TypeSystem.TypeDesc GetWritableDataType(ISymbolNode node)
        => node is WritableDataNode writable ? writable.Type : null;
}
