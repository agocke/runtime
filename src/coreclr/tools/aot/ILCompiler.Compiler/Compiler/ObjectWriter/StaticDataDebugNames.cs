// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler.ObjectWriter;

public abstract partial class ObjectWriter
{
    private protected virtual Utf8String GetStaticVariableName(FieldDesc field)
    {
        if (field.IsThreadStatic)
            return _nodeFactory.NameMangler.NodeMangler.ThreadStatics(field.OwningType);

        return field.HasGCStaticBase
            ? _nodeFactory.NameMangler.NodeMangler.GCStatics(field.OwningType)
            : _nodeFactory.NameMangler.NodeMangler.NonGCStatics(field.OwningType);
    }
}
