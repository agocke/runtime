// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;

namespace Microsoft.NET.HostModel.MachO;

/// <summary>
/// See https://github.com/apple-oss-distributions/Security/blob/3dab46a11f45f2ffdbd70e2127cc5a8ce4a1f222/SecurityTool/sharedTool/codesign.c#L61
/// This is the base class for a super blob, which is a blob containing other blobs.
/// This class handles reading and writing of all the sub-blobs.
/// The blob contains the following structure:
/// </summary>
internal sealed class SuperBlob : IBlob
{
    private const uint HeaderSize = sizeof(uint) + sizeof(uint) + sizeof(uint); // magic + size + count
    private const uint IndexEntrySize = sizeof(uint) + sizeof(uint); // slot + offset

    public BlobMagic Magic { get; }

    private readonly List<(BlobIndex Index, IBlob Blob)> _entries = new();

    public uint Size
    {
        get
        {
            uint size = HeaderSize;
            size += (uint)(_entries.Count * IndexEntrySize);
            foreach (var entry in _entries)
            {
                size += entry.Blob.Size;
            }
            return size;
        }
    }

    public SuperBlob(BlobMagic magic)
    {
        Magic = magic;
    }

    public IReadOnlyList<IBlob> Blobs => _entries.Select(e => e.Blob).ToList();
    public IReadOnlyList<BlobIndex> Indices => _entries.Select(e => e.Index).ToList();

    public void AddBlob(IBlob blob, CodeDirectorySpecialSlot slot)
    {
        int insertionIndex = 0;
        while (insertionIndex < _entries.Count && _entries[insertionIndex].Index.Slot < slot)
        {
            insertionIndex++;
        }
        if (insertionIndex < _entries.Count && _entries[insertionIndex].Index.Slot == slot)
        {
            throw new ArgumentException($"Blob with slot {slot} already exists");
        }
        var blobIndex = new BlobIndex(slot, 0);
        _entries.Insert(insertionIndex, (blobIndex, blob));
        RecalculateOffsets();
    }

    public IBlob? GetBlob(CodeDirectorySpecialSlot slot)
    {
        foreach (var entry in _entries)
        {
            if (entry.Index.Slot == slot)
                return entry.Blob;
        }
        return null;
    }

    public IBlob? GetBlobByMagic(BlobMagic magic)
    {
        return _entries.FirstOrDefault(e => e.Blob.Magic == magic).Blob;
    }

    public int Write(IMachOFileWriter file, long offset)
    {
        int bytesWritten = 0;
        file.WriteUInt32BigEndian(offset, (uint)Magic);
        bytesWritten += sizeof(uint);
        file.WriteUInt32BigEndian(offset + bytesWritten, Size);
        bytesWritten += sizeof(uint);
        file.WriteUInt32BigEndian(offset + bytesWritten, (uint)_entries.Count);
        bytesWritten += sizeof(uint);
        foreach (var entry in _entries)
        {
            file.WriteUInt32BigEndian(offset + bytesWritten, (uint)entry.Index.Slot);
            bytesWritten += sizeof(uint);
            file.WriteUInt32BigEndian(offset + bytesWritten, entry.Index.Offset);
            bytesWritten += sizeof(uint);
        }
        foreach (var entry in _entries)
        {
            bytesWritten += entry.Blob.Write(file, offset + bytesWritten);
        }
        return bytesWritten;
    }

    private void RecalculateOffsets()
    {
        uint currentOffset = HeaderSize + (uint)(_entries.Count * IndexEntrySize);
        for (int i = 0; i < _entries.Count; i++)
        {
            var (index, blob) = _entries[i];
            _entries[i] = (new BlobIndex(index.Slot, currentOffset), blob);
            currentOffset += blob.Size;
        }
    }

    /// <summary>
    /// Creates a SuperBlob by reading from a memory-mapped file.
    /// </summary>
    public static SuperBlob Read(IMachOFileReader reader, long offset)
    {
        var magic = (BlobMagic)reader.ReadUInt32BigEndian(offset);
        var count = reader.ReadUInt32BigEndian(offset + sizeof(uint) * 2);
        var superBlob = new SuperBlob(magic);
        long indexOffset = offset + HeaderSize;
        var indices = new List<BlobIndex>();
        for (int i = 0; i < count; i++)
        {
            var slot = (CodeDirectorySpecialSlot)reader.ReadUInt32BigEndian(indexOffset);
            var blobOffset = reader.ReadUInt32BigEndian(indexOffset + sizeof(uint));
            indices.Add(new BlobIndex(slot, blobOffset));
            indexOffset += IndexEntrySize;
        }
        foreach (var index in indices)
        {
            var blobOffset = offset + index.Offset;
            var blob = SimpleBlob.Read(reader, blobOffset);
            superBlob.AddBlob(blob, index.Slot);
        }
        return superBlob;
    }
}
