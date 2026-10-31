// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using ILCompiler.DependencyAnalysis;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler.ObjectWriter;

public abstract partial class ObjectWriter
{
    private int _compactDataSymbolId;
    private readonly Dictionary<TypeDesc, Utf8String> _compactTypeNames = new();
    private readonly Dictionary<TypeDesc, Utf8String> _compactWritableNames = new();
    private readonly bool _compactDataSymbols =
        Environment.GetEnvironmentVariable("DOTNET_ILC_COMPACT_DATA_SYMBOLS") == "1";

    private protected virtual bool SupportsCompactDataSymbols => false;

    private bool TryGetCompactDataSymbol(ISymbolNode node, out Utf8String name)
    {
        if (!_compactDataSymbols || !SupportsCompactDataSymbols || !_isSingleFileCompilation ||
            _outputInfoBuilder is not null ||
            !(node is EETypeNode or GenericDictionaryNode or SealedVTableNode
                or GCStaticsNode or NonGCStaticsNode or ThreadStaticsNode ||
                EETypeNode.GetWritableDataType(node) is not null))
        {
            name = default;
            return false;
        }

        TypeDesc type = node is EETypeNode eeType ? eeType.Type : EETypeNode.GetWritableDataType(node);
        Dictionary<TypeDesc, Utf8String> names = node is EETypeNode ? _compactTypeNames : _compactWritableNames;
        if (type is not null && names.TryGetValue(type, out name))
            return true;

        int id = checked(++_compactDataSymbolId);
        _utf8StringBuilder.Clear().Append(".L__aot_data_");
        _utf8StringBuilder.Append(id.ToString("x", CultureInfo.InvariantCulture));
        name = _utf8StringBuilder.ToUtf8String();
        if (type is not null)
            names.Add(type, name);

        return true;
    }
}
