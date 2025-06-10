// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.NET.HostModel.MachO;

/// <summary>
/// Parser reading blobs from a Mach-O file.
/// </summary>
internal static class BlobParser
{
    /// <summary>
    /// Reads a blob from a file at the specified offset.
    /// </summary>
    /// <param name="reader">The memory-mapped view accessor to read from.</param>
    /// <param name="offset">The offset to start reading from.</param>
    /// <returns>The created blob.</returns>
    public static IBlob ReadBlob(IMachOFileReader reader, long offset)
    {
        var magic = (BlobMagic)reader.ReadUInt32BigEndian(offset);
        return magic switch
        {
            BlobMagic.CodeDirectory => new CodeDirectoryBlob(SimpleBlob.Read(reader, offset)),
            BlobMagic.Requirements => new RequirementsBlob(SuperBlob.Read(reader, offset)),
            BlobMagic.Entitlements => new EntitlementsBlob(SimpleBlob.Read(reader, offset)),
            BlobMagic.DerEntitlements => new DerEntitlementsBlob(SimpleBlob.Read(reader, offset)),
            BlobMagic.CmsWrapper => new CmsWrapperBlob(SimpleBlob.Read(reader, offset)),
            BlobMagic.EmbeddedSignature => new EmbeddedSignatureBlob(SuperBlob.Read(reader, offset)),
            _ => SimpleBlob.Read(reader, offset)
        };
    }

}

/// <summary>
/// Factory for creating specialized blobs with validation and additional behavior.
/// </summary>
internal static class BlobFactory
{
    private const int MaxEntitlementsBlobSize = 2048;

    /// <summary>
    /// Creates an entitlements blob with validation.
    /// </summary>
    public static SimpleBlob CreateEntitlementsBlob(byte[] data)
    {
        if (data.Length > MaxEntitlementsBlobSize)
            throw new System.ArgumentException($"Entitlements blob data exceeds maximum size of {MaxEntitlementsBlobSize} bytes");
        return new SimpleBlob(BlobMagic.Entitlements, data);
    }

    /// <summary>
    /// Creates a DER entitlements blob with validation.
    /// </summary>
    public static SimpleBlob CreateDerEntitlementsBlob(byte[] data)
    {
        if (data.Length > MaxEntitlementsBlobSize)
            throw new System.ArgumentException($"DER Entitlements blob data exceeds maximum size of {MaxEntitlementsBlobSize} bytes");
        return new SimpleBlob(BlobMagic.DerEntitlements, data);
    }

    /// <summary>
    /// Creates an embedded signature super blob with all required components.
    /// </summary>
    public static SuperBlob CreateEmbeddedSignatureBlob(
        SimpleBlob codeDirectoryBlob,
        SimpleBlob requirementsBlob,
        SimpleBlob cmsWrapperBlob,
        SimpleBlob entitlementsBlob = null,
        SimpleBlob derEntitlementsBlob = null)
    {
        if (codeDirectoryBlob.Magic != BlobMagic.CodeDirectory)
            throw new System.ArgumentException("Invalid magic value for code directory blob");
        if (requirementsBlob.Magic != BlobMagic.Requirements)
            throw new System.ArgumentException("Invalid magic value for requirements blob");
        if (cmsWrapperBlob.Magic != BlobMagic.CmsWrapper)
            throw new System.ArgumentException("Invalid magic value for CMS wrapper blob");
        if (entitlementsBlob != null && entitlementsBlob.Magic != BlobMagic.Entitlements)
            throw new System.ArgumentException("Invalid magic value for entitlements blob");
        if (derEntitlementsBlob != null && derEntitlementsBlob.Magic != BlobMagic.DerEntitlements)
            throw new System.ArgumentException("Invalid magic value for DER entitlements blob");

        var superBlob = new SuperBlob(BlobMagic.EmbeddedSignature);
        superBlob.AddBlob(codeDirectoryBlob, CodeDirectorySpecialSlot.CodeDirectory);
        superBlob.AddBlob(requirementsBlob, CodeDirectorySpecialSlot.Requirements);
        superBlob.AddBlob(cmsWrapperBlob, CodeDirectorySpecialSlot.CmsWrapper);
        if (entitlementsBlob != null)
            superBlob.AddBlob(entitlementsBlob, CodeDirectorySpecialSlot.Entitlements);
        if (derEntitlementsBlob != null)
            superBlob.AddBlob(derEntitlementsBlob, CodeDirectorySpecialSlot.DerEntitlements);
        return superBlob;
    }
}
