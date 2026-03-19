// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ILCompiler.Compiler.Tests
{
    /// <summary>
    /// Helpers for constructing minimal ELF objects and ar archives in memory
    /// for linker unit tests.
    /// </summary>
    internal static class ElfTestData
    {
        // Minimal ELF64 relocatable object with:
        //   - .text section with given code bytes
        //   - .symtab and .strtab
        //   - Optionally: .rela.text with relocations
        //   - Optionally: symbols (global, weak, local)
        public static byte[] BuildMinimalElf(
            ushort machine = 62, // EM_X86_64
            ElfTestSymbol[] symbols = null,
            byte[] textData = null,
            byte[] dataSection = null,
            ElfTestRela[] relocations = null,
            ElfTestComdatGroup[] comdatGroups = null)
        {
            symbols ??= Array.Empty<ElfTestSymbol>();
            textData ??= new byte[] { 0xCC }; // int3

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

            // We'll build section headers as we go, then write them at the end
            var sections = new List<SectionInfo>();
            var sectionDataBlobs = new List<byte[]>();

            // Section 0: null section
            sections.Add(new SectionInfo());
            sectionDataBlobs.Add(Array.Empty<byte>());

            // Section 1: .text
            sections.Add(new SectionInfo
            {
                Name = ".text",
                Type = 1, // SHT_PROGBITS
                Flags = 0x6, // SHF_ALLOC | SHF_EXECINSTR
                Alignment = 16,
            });
            sectionDataBlobs.Add(textData);

            int dataSectionIndex = -1;
            if (dataSection is not null)
            {
                dataSectionIndex = sections.Count;
                sections.Add(new SectionInfo
                {
                    Name = ".data",
                    Type = 1, // SHT_PROGBITS
                    Flags = 0x3, // SHF_ALLOC | SHF_WRITE
                    Alignment = 8,
                });
                sectionDataBlobs.Add(dataSection);
            }

            // Build the section name string table (.shstrtab)
            var shstrtab = new MemoryStream();
            shstrtab.WriteByte(0); // null entry
            var nameOffsets = new Dictionary<string, uint>();
            foreach (var sec in sections)
            {
                if (sec.Name is null) continue;
                nameOffsets[sec.Name] = (uint)shstrtab.Position;
                byte[] nameBytes = Encoding.ASCII.GetBytes(sec.Name);
                shstrtab.Write(nameBytes);
                shstrtab.WriteByte(0);
            }

            // We'll add more sections below — register their names now
            foreach (string name in new[] { ".symtab", ".strtab", ".shstrtab", ".rela.text" })
            {
                nameOffsets[name] = (uint)shstrtab.Position;
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                shstrtab.Write(nameBytes);
                shstrtab.WriteByte(0);
            }

            // Build symbol string table (.strtab)
            var strtab = new MemoryStream();
            strtab.WriteByte(0); // null entry
            var symNameOffsets = new Dictionary<string, uint>();
            foreach (var sym in symbols)
            {
                if (sym.Name is not null && !symNameOffsets.ContainsKey(sym.Name))
                {
                    symNameOffsets[sym.Name] = (uint)strtab.Position;
                    byte[] nameBytes = Encoding.ASCII.GetBytes(sym.Name);
                    strtab.Write(nameBytes);
                    strtab.WriteByte(0);
                }
            }

            // Build symbol table (.symtab) — 24 bytes per entry for ELF64
            var symtab = new MemoryStream();
            // Entry 0: null symbol
            symtab.Write(new byte[24]);

            // Sort: locals first, then globals/weaks
            var locals = new List<ElfTestSymbol>();
            var globals = new List<ElfTestSymbol>();
            foreach (var sym in symbols)
            {
                if (sym.Binding == 0) // STB_LOCAL
                    locals.Add(sym);
                else
                    globals.Add(sym);
            }

            uint localCount = 1; // null symbol counts as local
            foreach (var sym in locals)
            {
                WriteSymbol(symtab, sym, symNameOffsets);
                localCount++;
            }
            foreach (var sym in globals)
            {
                WriteSymbol(symtab, sym, symNameOffsets);
            }

            // Build .rela.text if there are relocations
            byte[] relaData = null;
            if (relocations is not null && relocations.Length > 0)
            {
                var relaStream = new MemoryStream();
                foreach (var rela in relocations)
                {
                    Span<byte> entry = stackalloc byte[24];
                    BinaryPrimitives.WriteUInt64LittleEndian(entry, rela.Offset);
                    ulong info = ((ulong)rela.SymbolIndex << 32) | rela.Type;
                    BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8), info);
                    BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(16), rela.Addend);
                    relaStream.Write(entry);
                }
                relaData = relaStream.ToArray();
            }

            // Add strtab section
            int strtabIndex = sections.Count;
            sections.Add(new SectionInfo
            {
                Name = ".strtab",
                Type = 3, // SHT_STRTAB
                Alignment = 1,
            });
            sectionDataBlobs.Add(strtab.ToArray());

            // Add symtab section
            int symtabIndex = sections.Count;
            sections.Add(new SectionInfo
            {
                Name = ".symtab",
                Type = 2, // SHT_SYMTAB
                Link = (uint)strtabIndex,
                Info = localCount,
                Alignment = 8,
                EntrySize = 24,
            });
            sectionDataBlobs.Add(symtab.ToArray());

            // Add .rela.text if present
            if (relaData is not null)
            {
                sections.Add(new SectionInfo
                {
                    Name = ".rela.text",
                    Type = 4, // SHT_RELA
                    Flags = 0x40, // SHF_INFO_LINK
                    Link = (uint)symtabIndex,
                    Info = 1, // target section index (.text)
                    Alignment = 8,
                    EntrySize = 24,
                });
                sectionDataBlobs.Add(relaData);
            }

            // Add .shstrtab section
            int shstrtabIndex = sections.Count;
            sections.Add(new SectionInfo
            {
                Name = ".shstrtab",
                Type = 3, // SHT_STRTAB
                Alignment = 1,
            });
            sectionDataBlobs.Add(shstrtab.ToArray());

            // Assign name indices
            foreach (var sec in sections)
            {
                if (sec.Name is not null && nameOffsets.TryGetValue(sec.Name, out uint offset))
                    sec.NameIndex = offset;
            }

            // Now write the ELF file
            // ELF header (64 bytes)
            byte[] elfHeader = new byte[64];
            elfHeader[0] = 0x7F; elfHeader[1] = (byte)'E'; elfHeader[2] = (byte)'L'; elfHeader[3] = (byte)'F';
            elfHeader[4] = 2; // ELFCLASS64
            elfHeader[5] = 1; // ELFDATA2LSB
            elfHeader[6] = 1; // EV_CURRENT
            BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(16), 1); // ET_REL
            BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(18), machine);
            BinaryPrimitives.WriteUInt32LittleEndian(elfHeader.AsSpan(20), 1); // EV_CURRENT
            // e_entry, e_phoff = 0
            // e_shoff — patch later
            BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(52), 64); // e_ehsize
            BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(58), 64); // e_shentsize
            BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(60), (ushort)sections.Count); // e_shnum
            BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(62), (ushort)shstrtabIndex); // e_shstrndx

            bw.Write(elfHeader);

            // Write section data blobs, recording offsets
            for (int i = 0; i < sections.Count; i++)
            {
                if (sectionDataBlobs[i].Length > 0)
                {
                    // Align to 8 bytes
                    while (ms.Position % 8 != 0) ms.WriteByte(0);
                    sections[i].FileOffset = (ulong)ms.Position;
                    sections[i].Size = (ulong)sectionDataBlobs[i].Length;
                    bw.Write(sectionDataBlobs[i]);
                }
            }

            // Write section headers
            while (ms.Position % 8 != 0) ms.WriteByte(0);
            ulong shoff = (ulong)ms.Position;

            foreach (var sec in sections)
            {
                WriteSectionHeader(bw, sec);
            }

            // Patch e_shoff
            ms.Position = 40;
            bw.Write(shoff);

            return ms.ToArray();
        }

        /// <summary>
        /// Builds a minimal ar archive from the given named members.
        /// </summary>
        public static byte[] BuildArchive(params (string Name, byte[] Data)[] members)
        {
            return BuildArchive(members, symbolTable: null);
        }

        /// <summary>
        /// Builds a minimal ar archive with an optional symbol table.
        /// </summary>
        public static byte[] BuildArchive(
            (string Name, byte[] Data)[] members,
            (string SymbolName, int MemberIndex)[] symbolTable)
        {
            using var ms = new MemoryStream();

            // Magic
            ms.Write("!<arch>\n"u8);

            // Track member header offsets for the symbol table
            var memberHeaderOffsets = new List<int>();

            // If there's a symbol table, write it first
            if (symbolTable is not null && symbolTable.Length > 0)
            {
                // We need to predict member offsets, so compute symbol table size first
                // Symbol table: 4 bytes (count) + 4*N (offsets) + string data
                var symStrings = new MemoryStream();
                foreach (var (symName, _) in symbolTable)
                {
                    symStrings.Write(Encoding.ASCII.GetBytes(symName));
                    symStrings.WriteByte(0);
                }
                int symTableDataSize = 4 + symbolTable.Length * 4 + (int)symStrings.Length;
                int symTablePadded = symTableDataSize + (symTableDataSize % 2);

                // First member offset is: 8 (magic) + 60 (symtab header) + symTablePadded
                int firstMemberOffset = 8 + 60 + symTablePadded;

                // Compute all member offsets
                int offset = firstMemberOffset;
                for (int i = 0; i < members.Length; i++)
                {
                    memberHeaderOffsets.Add(offset);
                    int memberPadded = members[i].Data.Length + (members[i].Data.Length % 2);
                    offset += 60 + memberPadded;
                }

                // Write symbol table member
                WriteArchiveMemberHeader(ms, "/", symTableDataSize);
                var symTableData = new byte[symTableDataSize];
                BinaryPrimitives.WriteInt32BigEndian(symTableData, symbolTable.Length);
                for (int i = 0; i < symbolTable.Length; i++)
                {
                    BinaryPrimitives.WriteInt32BigEndian(
                        symTableData.AsSpan(4 + i * 4),
                        memberHeaderOffsets[symbolTable[i].MemberIndex]);
                }
                symStrings.ToArray().CopyTo(symTableData.AsSpan(4 + symbolTable.Length * 4));
                ms.Write(symTableData);
                if (symTableDataSize % 2 != 0) ms.WriteByte((byte)'\n');
            }

            // Write regular members
            foreach (var (name, data) in members)
            {
                if (memberHeaderOffsets.Count == 0)
                    memberHeaderOffsets.Add((int)ms.Position);

                WriteArchiveMemberHeader(ms, name + "/", data.Length);
                ms.Write(data);
                if (data.Length % 2 != 0) ms.WriteByte((byte)'\n');
            }

            return ms.ToArray();
        }

        private static void WriteArchiveMemberHeader(Stream ms, string name, int size)
        {
            // Name: 16 bytes, right-padded with spaces
            byte[] header = new byte[60];
            Encoding.ASCII.GetBytes(name).CopyTo(header.AsSpan());
            for (int i = name.Length; i < 16; i++) header[i] = (byte)' ';
            // Date (12), UID (6), GID (6), Mode (8): all spaces
            for (int i = 16; i < 48; i++) header[i] = (byte)' ';
            // Size: 10 bytes
            string sizeStr = size.ToString().PadRight(10);
            Encoding.ASCII.GetBytes(sizeStr).CopyTo(header.AsSpan(48));
            // End marker
            header[58] = (byte)'`';
            header[59] = (byte)'\n';
            ms.Write(header);
        }

        private static void WriteSymbol(MemoryStream ms, ElfTestSymbol sym, Dictionary<string, uint> nameOffsets)
        {
            Span<byte> entry = stackalloc byte[24];
            entry.Clear();

            uint nameOff = sym.Name is not null && nameOffsets.TryGetValue(sym.Name, out uint off) ? off : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(entry, nameOff);
            entry[4] = (byte)((sym.Binding << 4) | sym.Type);
            entry[5] = sym.Visibility;
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(6), sym.SectionIndex);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8), sym.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(16), sym.Size);
            ms.Write(entry);
        }

        private static void WriteSectionHeader(BinaryWriter bw, SectionInfo sec)
        {
            byte[] sh = new byte[64];
            BinaryPrimitives.WriteUInt32LittleEndian(sh.AsSpan(0), sec.NameIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(sh.AsSpan(4), sec.Type);
            BinaryPrimitives.WriteUInt64LittleEndian(sh.AsSpan(8), sec.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(sh.AsSpan(16), 0); // addr
            BinaryPrimitives.WriteUInt64LittleEndian(sh.AsSpan(24), sec.FileOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(sh.AsSpan(32), sec.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(sh.AsSpan(40), sec.Link);
            BinaryPrimitives.WriteUInt32LittleEndian(sh.AsSpan(44), sec.Info);
            BinaryPrimitives.WriteUInt64LittleEndian(sh.AsSpan(48), sec.Alignment);
            BinaryPrimitives.WriteUInt64LittleEndian(sh.AsSpan(56), sec.EntrySize);
            bw.Write(sh);
        }

        private sealed class SectionInfo
        {
            public string Name;
            public uint NameIndex;
            public uint Type;
            public ulong Flags;
            public ulong FileOffset;
            public ulong Size;
            public uint Link;
            public uint Info;
            public ulong Alignment;
            public ulong EntrySize;
        }
    }

    internal sealed class ElfTestSymbol
    {
        public string Name { get; init; }
        public byte Binding { get; init; } // 0=local, 1=global, 2=weak
        public byte Type { get; init; } // 0=notype, 1=object, 2=func
        public byte Visibility { get; init; }
        public ushort SectionIndex { get; init; } // 0=undefined, 1=.text, etc.
        public ulong Value { get; init; }
        public ulong Size { get; init; }
    }

    internal sealed class ElfTestRela
    {
        public ulong Offset { get; init; }
        public uint SymbolIndex { get; init; } // index into symbol table (1-based, 0 is null)
        public uint Type { get; init; }
        public long Addend { get; init; }
    }

    internal sealed class ElfTestComdatGroup
    {
        public string Name { get; init; }
        public int[] SectionIndices { get; init; }
    }
}
