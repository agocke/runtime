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
    /// Emits a complete ELF executable by combining linked sections, resolved
    /// symbols, and program headers.
    /// </summary>
    internal sealed class ElfExecutableWriter
    {
        private const ulong PageSize = 0x1000;
        private const int Elf64HeaderSize = 64;
        private const int Elf64ProgramHeaderSize = 56;
        private const int Elf64SectionHeaderSize = 64;

        private readonly SectionLayout _layout;
        private readonly ushort _machine;
        private readonly ulong _entryPoint;
        private readonly string _interpreter;

        public ElfExecutableWriter(SectionLayout layout, ushort machine, ulong entryPoint, string interpreter = null)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _machine = machine;
            _entryPoint = entryPoint;
            _interpreter = interpreter;
        }

        /// <summary>
        /// Writes the ELF executable to the given stream.
        /// </summary>
        /// <param name="output">Output stream.</param>
        /// <param name="sectionData">
        /// Final section content keyed by merged section name.
        /// </param>
        public void Write(Stream output, Dictionary<string, byte[]> sectionData)
        {
            ArgumentNullException.ThrowIfNull(output);

            var programHeaders = BuildProgramHeaders();
            int phCount = programHeaders.Count;
            int shCount = _layout.MergedSections.Count + 1; // +1 for null section

            // ELF header
            WriteElfHeader(output, (ulong)Elf64HeaderSize, (ushort)phCount, (ushort)shCount);

            // Program headers
            foreach (var ph in programHeaders)
            {
                WriteProgramHeader(output, ph);
            }

            // Section content (aligned to page boundaries)
            foreach (MergedSection section in _layout.MergedSections)
            {
                PadToAlignment(output, PageSize);
                if (sectionData.TryGetValue(section.Name, out byte[] data))
                {
                    output.Write(data);
                }
                else if (section.Type != SHT_NOBITS)
                {
                    // Write zeros for sections without explicit data
                    output.Write(new byte[(int)section.TotalSize]);
                }
            }

            // Section headers at end of file
            PadToAlignment(output, 8);
            long shOffset = output.Position;

            // Null section header
            output.Write(new byte[Elf64SectionHeaderSize]);

            foreach (MergedSection section in _layout.MergedSections)
            {
                WriteSectionHeader(output, section);
            }

            // Patch section header offset in ELF header
            long savedPos = output.Position;
            output.Position = 40; // e_shoff in ELF64
            Span<byte> shOffsetBytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(shOffsetBytes, (ulong)shOffset);
            output.Write(shOffsetBytes);
            output.Position = savedPos;
        }

        private List<ProgramHeader> BuildProgramHeaders()
        {
            var headers = new List<ProgramHeader>(_layout.ProgramHeaders);

            // Add PT_INTERP if an interpreter is specified
            if (_interpreter is not null)
            {
                headers.Insert(0, new ProgramHeader(
                    type: ProgramHeader.PT_INTERP,
                    flags: ProgramHeader.PF_R,
                    virtualAddress: 0,
                    size: (ulong)Encoding.ASCII.GetByteCount(_interpreter) + 1,
                    alignment: 1));
            }

            return headers;
        }

        private void WriteElfHeader(Stream output, ulong phOffset, ushort phCount, ushort shCount)
        {
            Span<byte> header = stackalloc byte[Elf64HeaderSize];
            header.Clear();

            // ELF magic
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = ELFCLASS64;
            header[5] = ELFDATA2LSB;
            header[6] = EV_CURRENT;
            // OSABI and padding: zeros

            // e_type: ET_EXEC = 2
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(16), 2);
            // e_machine
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(18), _machine);
            // e_version
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20), EV_CURRENT);
            // e_entry
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24), _entryPoint);
            // e_phoff
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(32), phOffset);
            // e_shoff — will be patched later
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40), 0);
            // e_flags
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(48), 0);
            // e_ehsize
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(52), Elf64HeaderSize);
            // e_phentsize
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(54), Elf64ProgramHeaderSize);
            // e_phnum
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(56), phCount);
            // e_shentsize
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(58), Elf64SectionHeaderSize);
            // e_shnum
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(60), shCount);
            // e_shstrndx
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(62), 0);

            output.Write(header);
        }

        private static void WriteProgramHeader(Stream output, ProgramHeader ph)
        {
            Span<byte> data = stackalloc byte[Elf64ProgramHeaderSize];
            data.Clear();

            BinaryPrimitives.WriteUInt32LittleEndian(data, ph.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(4), ph.Flags);
            // p_offset — 0 for now (simplified)
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(8), 0);
            // p_vaddr
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(16), ph.VirtualAddress);
            // p_paddr
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(24), ph.VirtualAddress);
            // p_filesz
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(32), ph.Size);
            // p_memsz
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(40), ph.Size);
            // p_align
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(48), ph.Alignment);

            output.Write(data);
        }

        private static void WriteSectionHeader(Stream output, MergedSection section)
        {
            Span<byte> data = stackalloc byte[Elf64SectionHeaderSize];
            data.Clear();

            // sh_name — 0 (no string table in this simplified writer)
            BinaryPrimitives.WriteUInt32LittleEndian(data, 0);
            // sh_type
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(4), section.Type);
            // sh_flags
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(8), section.Flags);
            // sh_addr
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(16), section.VirtualAddress);
            // sh_offset — 0 for now
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(24), 0);
            // sh_size
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(32), section.TotalSize);
            // sh_link, sh_info
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(40), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(44), 0);
            // sh_addralign
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(48), PageSize);
            // sh_entsize
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(56), 0);

            output.Write(data);
        }

        private static void PadToAlignment(Stream output, ulong alignment)
        {
            long pos = output.Position;
            long aligned = (long)((ulong)(pos + (long)alignment - 1) & ~(alignment - 1));
            if (aligned > pos)
            {
                output.Write(new byte[aligned - pos]);
            }
        }
    }
}
