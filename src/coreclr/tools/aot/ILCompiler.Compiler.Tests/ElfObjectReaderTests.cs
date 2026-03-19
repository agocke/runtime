// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using ILCompiler.Linker;
using Xunit;

namespace ILCompiler.Compiler.Tests
{
    public class ElfObjectReaderTests
    {
        [Fact]
        public void ParsesMinimalElfObject()
        {
            byte[] elf = ElfTestData.BuildMinimalElf();

            var reader = new ElfObjectReader(elf);

            Assert.Equal(62, reader.Machine); // EM_X86_64
            Assert.True(reader.Sections.Count > 0);
        }

        [Fact]
        public void ParsesCorrectMachine()
        {
            byte[] elfX64 = ElfTestData.BuildMinimalElf(machine: 62);
            byte[] elfArm64 = ElfTestData.BuildMinimalElf(machine: 183);

            var readerX64 = new ElfObjectReader(elfX64);
            var readerArm64 = new ElfObjectReader(elfArm64);

            Assert.Equal(62, readerX64.Machine);
            Assert.Equal(183, readerArm64.Machine);
        }

        [Fact]
        public void FindsTextSection()
        {
            byte[] textData = new byte[] { 0x48, 0x89, 0xE5, 0xC3 }; // mov rbp, rsp; ret
            byte[] elf = ElfTestData.BuildMinimalElf(textData: textData);

            var reader = new ElfObjectReader(elf);
            ElfSection text = reader.Sections.FirstOrDefault(s => s.Name == ".text");

            Assert.NotNull(text);
            Assert.True(text.IsAllocatable);
            Assert.True(text.IsExecutable);
            Assert.False(text.IsWritable);
            Assert.Equal((ulong)textData.Length, text.Size);
        }

        [Fact]
        public void ReadsTextSectionData()
        {
            byte[] expected = new byte[] { 0x48, 0x89, 0xE5, 0xC3 };
            byte[] elf = ElfTestData.BuildMinimalElf(textData: expected);

            var reader = new ElfObjectReader(elf);
            ElfSection text = reader.Sections.First(s => s.Name == ".text");
            ReadOnlySpan<byte> actual = reader.GetSectionData(text);

            Assert.True(actual.SequenceEqual(expected));
        }

        [Fact]
        public void FindsDataSection()
        {
            byte[] data = new byte[] { 0x01, 0x00, 0x00, 0x00 };
            byte[] elf = ElfTestData.BuildMinimalElf(dataSection: data);

            var reader = new ElfObjectReader(elf);
            ElfSection dataSection = reader.Sections.FirstOrDefault(s => s.Name == ".data");

            Assert.NotNull(dataSection);
            Assert.True(dataSection.IsAllocatable);
            Assert.True(dataSection.IsWritable);
            Assert.False(dataSection.IsExecutable);
        }

        [Fact]
        public void ParsesGlobalSymbol()
        {
            byte[] elf = ElfTestData.BuildMinimalElf(
                symbols: new[]
                {
                    new ElfTestSymbol { Name = "_start", Binding = 1, Type = 2, SectionIndex = 1, Value = 0 }
                });

            var reader = new ElfObjectReader(elf);
            ElfSymbolEntry sym = reader.Symbols.FirstOrDefault(s => s.Name == "_start");

            Assert.NotNull(sym);
            Assert.True(sym.IsGlobal);
            Assert.True(sym.IsFunction);
            Assert.False(sym.IsUndefined);
        }

        [Fact]
        public void ParsesWeakSymbol()
        {
            byte[] elf = ElfTestData.BuildMinimalElf(
                symbols: new[]
                {
                    new ElfTestSymbol { Name = "weakfn", Binding = 2, Type = 2, SectionIndex = 1, Value = 0 }
                });

            var reader = new ElfObjectReader(elf);
            ElfSymbolEntry sym = reader.Symbols.FirstOrDefault(s => s.Name == "weakfn");

            Assert.NotNull(sym);
            Assert.True(sym.IsWeak);
            Assert.False(sym.IsGlobal);
        }

        [Fact]
        public void ParsesUndefinedSymbol()
        {
            byte[] elf = ElfTestData.BuildMinimalElf(
                symbols: new[]
                {
                    new ElfTestSymbol { Name = "extern_func", Binding = 1, Type = 0, SectionIndex = 0 }
                });

            var reader = new ElfObjectReader(elf);
            ElfSymbolEntry sym = reader.Symbols.FirstOrDefault(s => s.Name == "extern_func");

            Assert.NotNull(sym);
            Assert.True(sym.IsUndefined);
            Assert.True(sym.IsGlobal);
        }

        [Fact]
        public void ParsesMultipleSymbols()
        {
            byte[] elf = ElfTestData.BuildMinimalElf(
                symbols: new[]
                {
                    new ElfTestSymbol { Name = "local_var", Binding = 0, Type = 1, SectionIndex = 1, Value = 0 },
                    new ElfTestSymbol { Name = "main", Binding = 1, Type = 2, SectionIndex = 1, Value = 16 },
                    new ElfTestSymbol { Name = "printf", Binding = 1, Type = 0, SectionIndex = 0 },
                });

            var reader = new ElfObjectReader(elf);

            Assert.True(reader.Symbols.Count >= 3);
            Assert.NotNull(reader.Symbols.FirstOrDefault(s => s.Name == "local_var"));
            Assert.NotNull(reader.Symbols.FirstOrDefault(s => s.Name == "main"));
            Assert.NotNull(reader.Symbols.FirstOrDefault(s => s.Name == "printf"));
        }

        [Fact]
        public void ParsesRelocations()
        {
            byte[] textData = new byte[16]; // enough space for relocs
            byte[] elf = ElfTestData.BuildMinimalElf(
                textData: textData,
                symbols: new[]
                {
                    new ElfTestSymbol { Name = "target", Binding = 1, Type = 2, SectionIndex = 1, Value = 0 }
                },
                relocations: new[]
                {
                    new ElfTestRela { Offset = 4, SymbolIndex = 1, Type = 4, Addend = -4 } // R_X86_64_PLT32
                });

            var reader = new ElfObjectReader(elf);

            Assert.Single(reader.Relocations);
            Assert.Equal(4UL, reader.Relocations[0].Offset);
            Assert.Equal(1U, reader.Relocations[0].SymbolIndex);
            Assert.Equal(4U, reader.Relocations[0].Type); // R_X86_64_PLT32
            Assert.Equal(-4L, reader.Relocations[0].Addend);
        }

        [Fact]
        public void RejectsInvalidMagic()
        {
            byte[] badData = new byte[64];
            badData[0] = 0xFF;

            Assert.Throws<InvalidDataException>(() => new ElfObjectReader(badData));
        }

        [Fact]
        public void RejectsTruncatedFile()
        {
            Assert.Throws<InvalidDataException>(() => new ElfObjectReader(new byte[10]));
        }

        [Fact]
        public void RejectsNonRelocatableElf()
        {
            byte[] elf = ElfTestData.BuildMinimalElf();
            // Patch e_type from ET_REL (1) to ET_EXEC (2)
            elf[16] = 2;

            Assert.Throws<InvalidDataException>(() => new ElfObjectReader(elf));
        }

        [Fact]
        public void FindsSectionStringTable()
        {
            byte[] elf = ElfTestData.BuildMinimalElf();

            var reader = new ElfObjectReader(elf);
            ElfSection shstrtab = reader.Sections.FirstOrDefault(s => s.Name == ".shstrtab");

            Assert.NotNull(shstrtab);
            Assert.Equal(3u, shstrtab.Type); // SHT_STRTAB
        }
    }
}
