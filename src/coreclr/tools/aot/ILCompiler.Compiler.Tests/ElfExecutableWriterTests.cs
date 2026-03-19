// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ILCompiler.Linker;
using Xunit;
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Compiler.Tests
{
    public class ElfExecutableWriterTests
    {
        [Fact]
        public void WritesValidElfHeader()
        {
            var layout = new SectionLayout();
            layout.ComputeLayout();

            var writer = new ElfExecutableWriter(layout, machine: EM_X86_64, entryPoint: 0x401000);
            using var ms = new MemoryStream();
            writer.Write(ms, new Dictionary<string, byte[]>());

            byte[] data = ms.ToArray();

            // ELF magic
            Assert.Equal(0x7F, data[0]);
            Assert.Equal((byte)'E', data[1]);
            Assert.Equal((byte)'L', data[2]);
            Assert.Equal((byte)'F', data[3]);
            Assert.Equal(2, data[4]); // ELFCLASS64
            Assert.Equal(1, data[5]); // ELFDATA2LSB

            // e_type = ET_EXEC (2)
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16)));

            // e_machine = EM_X86_64
            Assert.Equal(EM_X86_64, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18)));

            // e_entry
            ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24));
            Assert.Equal(0x401000UL, entry);
        }

        [Fact]
        public void WritesCorrectMachine()
        {
            var layout = new SectionLayout();
            layout.ComputeLayout();

            var writer = new ElfExecutableWriter(layout, machine: EM_AARCH64, entryPoint: 0);
            using var ms = new MemoryStream();
            writer.Write(ms, new Dictionary<string, byte[]>());

            byte[] data = ms.ToArray();
            Assert.Equal(EM_AARCH64, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18)));
        }

        [Fact]
        public void WritesSectionContent()
        {
            var layout = new SectionLayout();
            var sections = new List<ElfSection>
            {
                new(0, 0, SHT_NULL, 0, 0, 0, 0, 0, 0, 0, 0) { Name = "" },
                new(1, 0, SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 0, 0, 4, 0, 0, 16, 0) { Name = ".text" },
            };
            layout.AddSections(0, sections);
            layout.ComputeLayout();

            byte[] textContent = new byte[] { 0xCC, 0xCC, 0xCC, 0xCC };
            var sectionData = new Dictionary<string, byte[]> { [".text"] = textContent };

            var writer = new ElfExecutableWriter(layout, EM_X86_64, 0x401000);
            using var ms = new MemoryStream();
            writer.Write(ms, sectionData);

            byte[] output = ms.ToArray();
            Assert.True(output.Length > 64);

            // The text content should appear somewhere in the output
            bool found = false;
            for (int i = 0; i <= output.Length - 4; i++)
            {
                if (output[i] == 0xCC && output[i + 1] == 0xCC &&
                    output[i + 2] == 0xCC && output[i + 3] == 0xCC)
                {
                    found = true;
                    break;
                }
            }
            Assert.True(found, "Section content not found in output");
        }

        [Fact]
        public void OutputSizeIsReasonable()
        {
            var layout = new SectionLayout();
            layout.ComputeLayout();

            var writer = new ElfExecutableWriter(layout, EM_X86_64, 0);
            using var ms = new MemoryStream();
            writer.Write(ms, new Dictionary<string, byte[]>());

            // An empty executable should still have the ELF header + section headers
            Assert.True(ms.Length >= 64);
            // But shouldn't be unreasonably large
            Assert.True(ms.Length < 8192);
        }

        [Fact]
        public void SectionHeaderOffsetIsValid()
        {
            var layout = new SectionLayout();
            layout.ComputeLayout();

            var writer = new ElfExecutableWriter(layout, EM_X86_64, 0);
            using var ms = new MemoryStream();
            writer.Write(ms, new Dictionary<string, byte[]>());

            byte[] output = ms.ToArray();
            ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(40));

            // Section header offset should be within the file
            Assert.True(shoff > 0);
            Assert.True(shoff < (ulong)output.Length);
        }

        [Fact]
        public void ProgramHeaderCountMatchesLayout()
        {
            var layout = new SectionLayout();
            var sections = new List<ElfSection>
            {
                new(0, 0, SHT_NULL, 0, 0, 0, 0, 0, 0, 0, 0) { Name = "" },
                new(1, 0, SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 0, 0, 100, 0, 0, 16, 0) { Name = ".text" },
                new(2, 0, SHT_PROGBITS, SHF_ALLOC | SHF_WRITE, 0, 0, 50, 0, 0, 8, 0) { Name = ".data" },
            };
            layout.AddSections(0, sections);
            layout.ComputeLayout();

            var writer = new ElfExecutableWriter(layout, EM_X86_64, 0x401000);
            using var ms = new MemoryStream();
            writer.Write(ms, new Dictionary<string, byte[]>());

            byte[] output = ms.ToArray();
            ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(56));

            Assert.Equal(layout.ProgramHeaders.Count, phnum);
        }
    }
}
