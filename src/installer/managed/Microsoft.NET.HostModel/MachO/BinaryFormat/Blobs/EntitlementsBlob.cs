// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.IO.MemoryMappedFiles;

namespace Microsoft.NET.HostModel.MachO;

/// <summary>
/// See https://github.com/apple-oss-distributions/Security/blob/3dab46a11f45f2ffdbd70e2127cc5a8ce4a1f222/OSX/libsecurity_utilities/lib/blob.h
/// Code signature data is always big endian / network order.
/// </summary>
internal class EntitlementsBlob : SimpleBlob
{
    public EntitlementsBlob(MemoryMappedViewAccessor accessor, long offset)
        : base(accessor, offset)
    {
        if (Magic != BlobMagic.Entitlements)
        {
            throw new InvalidDataException($"Invalid magic for EntitlementsBlob: {Magic}");
        }
    }

    public EntitlementsBlob(byte[] data)
        : base(BlobMagic.Entitlements, data)
    {
    }

    public override void Write(MemoryMappedViewAccessor accessor, long offset)
    {
        base.Write(accessor, offset);
    }

    public static uint MaxSize => 256;
}
