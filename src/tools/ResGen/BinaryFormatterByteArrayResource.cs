// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Resources;
using System.Resources.Extensions;

#nullable disable

internal sealed class BinaryFormatterByteArrayResource : IResource
{
    public string Name { get; }
    public string OriginatingFile { get; }
    public byte[] Bytes { get; }

    /// <summary>
    /// BinaryFormatter byte arrays contain the type name, but it is not directly accessible from the resx.
    /// </summary>
    public string TypeAssemblyQualifiedName => null;

    /// <summary>
    /// BinaryFormatter byte arrays contain the type name, but it is not directly accessible from the resx.
    /// </summary>
    public string TypeFullName => null;

    public BinaryFormatterByteArrayResource(string name, byte[] bytes, string originatingFile)
    {
        Name = name;
        Bytes = bytes;
        OriginatingFile = originatingFile;
    }

    public void AddTo(PreserializedResourceWriter writer)
    {
#pragma warning disable SYSLIB0011 // Type or member is obsolete
        writer.AddBinaryFormattedResource(Name, Bytes);
#pragma warning restore SYSLIB0011 // Type or member is obsolete
    }

    void IResource.AddTo(IResourceWriter writer) => AddTo((PreserializedResourceWriter)writer);
}
