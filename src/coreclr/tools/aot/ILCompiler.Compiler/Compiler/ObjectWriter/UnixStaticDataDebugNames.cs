// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using ILCompiler.DependencyAnalysis;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler.ObjectWriter;

abstract partial class UnixObjectWriter
{
    private protected override Utf8String GetStaticVariableName(FieldDesc field)
    {
        MetadataType owningType = (MetadataType)field.OwningType;
        ISymbolNode symbol;
        if (field.IsThreadStatic)
        {
            symbol = _nodeFactory.TypeThreadStaticsSymbol(owningType);
            if (symbol is ThreadStaticsNode { Marked: false })
                return default;
        }
        else
        {
            symbol = field.HasGCStaticBase
                ? _nodeFactory.TypeGCStaticsSymbol(owningType)
                : _nodeFactory.TypeNonGCStaticsSymbol(owningType);
        }

        symbol = _nodeFactory.ObjectInterner.GetDeduplicatedSymbol(_nodeFactory, symbol);
        return GetMangledName(symbol);
    }
}
