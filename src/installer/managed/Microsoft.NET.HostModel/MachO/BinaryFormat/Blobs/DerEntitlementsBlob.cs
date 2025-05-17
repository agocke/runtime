// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.NET.HostModel.MachO;

internal class DerEntitlementsBlob : SimpleBlob
{
    public DerEntitlementsBlob(MemoryMappedViewAccessor accessor, long offset)
        : base(accessor, offset)
    {
        if (Magic != BlobMagic.DerEntitlements)
        {
            throw new InvalidDataException($"Invalid magic for DerEntitlementsBlob: {Magic}");
        }
    }

    public DerEntitlementsBlob(byte[] data)
        : base(BlobMagic.DerEntitlements, data)
    {
    }
}
