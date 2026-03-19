// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Linker
{
    /// <summary>
    /// Reads ELF relocatable object files (.o) for the managed linker.
    /// </summary>
    /// <remarks>
    /// Parses ELF64 little-endian relocatable objects, extracting sections,
    /// symbols, and relocations needed for linking.
    /// </remarks>
    internal sealed class ElfObjectReader
    {
        private const int Elf64HeaderSize = 64;
        private const int Elf64SectionHeaderSize = 64;
        private const int Elf64SymbolSize = 24;
        private const int Elf64RelaSize = 24;

        private readonly byte[] _data;
        private ushort _machine;
        private readonly List<ElfSection> _sections = new();
        private readonly List<ElfSymbolEntry> _symbols = new();
        private readonly List<ElfRelaEntry> _relocations = new();
        private readonly List<int> _comdatGroupSections = new();

        public ElfObjectReader(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            Parse();
        }

        public ElfObjectReader(ReadOnlySpan<byte> data) : this(data.ToArray()) { }

        /// <summary>Target machine architecture (EM_X86_64, EM_AARCH64, etc.)</summary>
        public ushort Machine => _machine;

        /// <summary>All sections in the object file.</summary>
        public IReadOnlyList<ElfSection> Sections => _sections;

        /// <summary>All symbols in the object file.</summary>
        public IReadOnlyList<ElfSymbolEntry> Symbols => _symbols;

        /// <summary>All relocations (with explicit addends) in the object file.</summary>
        public IReadOnlyList<ElfRelaEntry> Relocations => _relocations;

        /// <summary>Indices of sections that are part of COMDAT groups.</summary>
        public IReadOnlyList<int> ComdatGroupSections => _comdatGroupSections;

        /// <summary>
        /// Gets the raw data for a section.
        /// </summary>
        public ReadOnlySpan<byte> GetSectionData(ElfSection section) =>
            section.Type != SHT_NOBITS
                ? _data.AsSpan(section.FileOffset, (int)section.Size)
                : ReadOnlySpan<byte>.Empty;

        private void Parse()
        {
            if (_data.Length < Elf64HeaderSize)
                throw new InvalidDataException("Data is too small to be an ELF file.");

            ParseHeader();
            ParseSectionHeaders();
            ParseSymbols();
            ParseRelocations();
            ParseComdatGroups();
        }

        private void ParseHeader()
        {
            ReadOnlySpan<byte> d = _data;

            // Verify ELF magic
            if (d[0] != 0x7F || d[1] != (byte)'E' || d[2] != (byte)'L' || d[3] != (byte)'F')
                throw new InvalidDataException("Invalid ELF magic.");

            // Verify 64-bit
            if (d[4] != ELFCLASS64)
                throw new InvalidDataException("Only ELF64 is supported.");

            // Verify little-endian
            if (d[5] != ELFDATA2LSB)
                throw new InvalidDataException("Only little-endian ELF is supported.");

            // Verify relocatable object
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(16));
            if (type != ET_REL)
                throw new InvalidDataException($"Expected ET_REL (1), got {type}.");

            _machine = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(18));
        }

        private void ParseSectionHeaders()
        {
            ReadOnlySpan<byte> d = _data;

            ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(40));
            ushort shentsize = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(58));
            ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(60));
            ushort shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(62));

            if (shoff == 0 || shnum == 0)
                return;

            // Parse section headers
            for (int i = 0; i < shnum; i++)
            {
                int offset = (int)shoff + i * shentsize;
                if (offset + Elf64SectionHeaderSize > _data.Length)
                    throw new InvalidDataException("Truncated section header table.");

                ReadOnlySpan<byte> sh = d.Slice(offset, Elf64SectionHeaderSize);
                uint nameIdx = BinaryPrimitives.ReadUInt32LittleEndian(sh);
                uint shType = BinaryPrimitives.ReadUInt32LittleEndian(sh.Slice(4));
                ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(sh.Slice(8));
                ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(sh.Slice(16));
                ulong fileOffset = BinaryPrimitives.ReadUInt64LittleEndian(sh.Slice(24));
                ulong size = BinaryPrimitives.ReadUInt64LittleEndian(sh.Slice(32));
                uint link = BinaryPrimitives.ReadUInt32LittleEndian(sh.Slice(40));
                uint info = BinaryPrimitives.ReadUInt32LittleEndian(sh.Slice(44));
                ulong alignment = BinaryPrimitives.ReadUInt64LittleEndian(sh.Slice(48));
                ulong entsize = BinaryPrimitives.ReadUInt64LittleEndian(sh.Slice(56));

                _sections.Add(new ElfSection(
                    index: i,
                    nameIndex: nameIdx,
                    type: shType,
                    flags: flags,
                    address: addr,
                    fileOffset: (int)fileOffset,
                    size: size,
                    link: link,
                    info: info,
                    alignment: alignment,
                    entrySize: entsize));
            }

            // Resolve section names from the string table
            if (shstrndx < _sections.Count && _sections[shstrndx].Type == SHT_STRTAB)
            {
                ElfSection strtab = _sections[shstrndx];
                foreach (ElfSection section in _sections)
                {
                    section.Name = ReadString(strtab.FileOffset, (int)strtab.Size, (int)section.NameIndex);
                }
            }
        }

        private void ParseSymbols()
        {
            foreach (ElfSection section in _sections)
            {
                if (section.Type != SHT_SYMTAB)
                    continue;

                ElfSection strtab = section.Link < (uint)_sections.Count ? _sections[(int)section.Link] : null;
                int count = section.EntrySize > 0 ? (int)(section.Size / section.EntrySize) : 0;

                for (int i = 0; i < count; i++)
                {
                    int offset = section.FileOffset + i * Elf64SymbolSize;
                    if (offset + Elf64SymbolSize > _data.Length)
                        break;

                    ReadOnlySpan<byte> sym = _data.AsSpan(offset, Elf64SymbolSize);
                    uint nameIdx = BinaryPrimitives.ReadUInt32LittleEndian(sym);
                    byte info = sym[4];
                    byte other = sym[5];
                    ushort shndx = BinaryPrimitives.ReadUInt16LittleEndian(sym.Slice(6));
                    ulong value = BinaryPrimitives.ReadUInt64LittleEndian(sym.Slice(8));
                    ulong size = BinaryPrimitives.ReadUInt64LittleEndian(sym.Slice(16));

                    string name = strtab is not null
                        ? ReadString(strtab.FileOffset, (int)strtab.Size, (int)nameIdx)
                        : "";

                    _symbols.Add(new ElfSymbolEntry(
                        name: name,
                        value: value,
                        size: size,
                        sectionIndex: shndx,
                        binding: (byte)(info >> 4),
                        type: (byte)(info & 0xF),
                        visibility: (byte)(other & 0x3)));
                }
            }
        }

        private void ParseRelocations()
        {
            foreach (ElfSection section in _sections)
            {
                if (section.Type != SHT_RELA)
                    continue;

                int targetSectionIndex = (int)section.Info;
                int count = section.EntrySize > 0 ? (int)(section.Size / section.EntrySize) : 0;

                for (int i = 0; i < count; i++)
                {
                    int offset = section.FileOffset + i * Elf64RelaSize;
                    if (offset + Elf64RelaSize > _data.Length)
                        break;

                    ReadOnlySpan<byte> rela = _data.AsSpan(offset, Elf64RelaSize);
                    ulong rOffset = BinaryPrimitives.ReadUInt64LittleEndian(rela);
                    ulong rInfo = BinaryPrimitives.ReadUInt64LittleEndian(rela.Slice(8));
                    long rAddend = BinaryPrimitives.ReadInt64LittleEndian(rela.Slice(16));

                    uint symIndex = (uint)(rInfo >> 32);
                    uint relocType = (uint)(rInfo & 0xFFFFFFFF);

                    _relocations.Add(new ElfRelaEntry(
                        targetSectionIndex: targetSectionIndex,
                        offset: rOffset,
                        symbolIndex: symIndex,
                        type: relocType,
                        addend: rAddend));
                }
            }
        }

        private void ParseComdatGroups()
        {
            foreach (ElfSection section in _sections)
            {
                if (section.Type != SHT_GROUP)
                    continue;

                int count = (int)(section.Size / 4);
                if (count < 2)
                    continue;

                ReadOnlySpan<byte> data = _data.AsSpan(section.FileOffset, (int)section.Size);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (flags != GRP_COMDAT)
                    continue;

                for (int i = 1; i < count; i++)
                {
                    uint memberIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4));
                    _comdatGroupSections.Add((int)memberIndex);
                }
            }
        }

        private string ReadString(int tableOffset, int tableSize, int nameOffset)
        {
            if (nameOffset >= tableSize)
                return "";

            int start = tableOffset + nameOffset;
            int end = start;
            while (end < _data.Length && _data[end] != 0)
                end++;

            return Encoding.ASCII.GetString(_data, start, end - start);
        }
    }

    /// <summary>
    /// Represents a section in an ELF object file.
    /// </summary>
    internal sealed class ElfSection
    {
        public int Index { get; }
        public uint NameIndex { get; }
        public string Name { get; set; }
        public uint Type { get; }
        public ulong Flags { get; }
        public ulong Address { get; }
        public int FileOffset { get; }
        public ulong Size { get; }
        public uint Link { get; }
        public uint Info { get; }
        public ulong Alignment { get; }
        public ulong EntrySize { get; }

        public bool IsAllocatable => (Flags & SHF_ALLOC) != 0;
        public bool IsWritable => (Flags & SHF_WRITE) != 0;
        public bool IsExecutable => (Flags & SHF_EXECINSTR) != 0;

        public ElfSection(int index, uint nameIndex, uint type, ulong flags, ulong address,
            int fileOffset, ulong size, uint link, uint info, ulong alignment, ulong entrySize)
        {
            Index = index;
            NameIndex = nameIndex;
            Type = type;
            Flags = flags;
            Address = address;
            FileOffset = fileOffset;
            Size = size;
            Link = link;
            Info = info;
            Alignment = alignment;
            EntrySize = entrySize;
        }
    }

    /// <summary>
    /// Represents a symbol table entry in an ELF object file.
    /// </summary>
    internal sealed class ElfSymbolEntry
    {
        public string Name { get; }
        public ulong Value { get; }
        public ulong Size { get; }
        public ushort SectionIndex { get; }
        public byte Binding { get; }
        public byte Type { get; }
        public byte Visibility { get; }

        public bool IsGlobal => Binding == STB_GLOBAL;
        public bool IsWeak => Binding == STB_WEAK;
        public bool IsLocal => Binding == STB_LOCAL;
        public bool IsUndefined => SectionIndex == (ushort)SHN_UNDEF;
        public bool IsFunction => Type == STT_FUNC;
        public bool IsObject => Type == STT_OBJECT;

        public ElfSymbolEntry(string name, ulong value, ulong size, ushort sectionIndex,
            byte binding, byte type, byte visibility)
        {
            Name = name;
            Value = value;
            Size = size;
            SectionIndex = sectionIndex;
            Binding = binding;
            Type = type;
            Visibility = visibility;
        }
    }

    /// <summary>
    /// Represents a relocation entry with explicit addend (SHT_RELA).
    /// </summary>
    internal sealed class ElfRelaEntry
    {
        public int TargetSectionIndex { get; }
        public ulong Offset { get; }
        public uint SymbolIndex { get; }
        public uint Type { get; }
        public long Addend { get; }

        public ElfRelaEntry(int targetSectionIndex, ulong offset, uint symbolIndex, uint type, long addend)
        {
            TargetSectionIndex = targetSectionIndex;
            Offset = offset;
            SymbolIndex = symbolIndex;
            Type = type;
            Addend = addend;
        }
    }
}
