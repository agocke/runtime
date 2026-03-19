// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using ILCompiler.Linker;
using Xunit;
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Compiler.Tests
{
    public class SectionLayoutTests
    {
        private static ElfSection MakeSection(int index, string name, uint type, ulong flags, ulong size, ulong alignment = 16) =>
            new(index, 0, type, flags, 0, 0, size, 0, 0, alignment, 0) { Name = name };

        [Fact]
        public void MergesSectionsWithSameName()
        {
            var layout = new SectionLayout();

            var sections0 = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 100),
            };
            var sections1 = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 200),
            };

            layout.AddSections(0, sections0);
            layout.AddSections(1, sections1);
            layout.ComputeLayout();

            Assert.Single(layout.MergedSections);
            Assert.Equal(".text", layout.MergedSections[0].Name);
            Assert.Equal(2, layout.MergedSections[0].Inputs.Count);
        }

        [Fact]
        public void SeparatesSectionsByName()
        {
            var layout = new SectionLayout();
            var sections = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 100),
                MakeSection(2, ".data", SHT_PROGBITS, SHF_ALLOC | SHF_WRITE, 50),
                MakeSection(3, ".rodata", SHT_PROGBITS, SHF_ALLOC, 25),
            };

            layout.AddSections(0, sections);
            layout.ComputeLayout();

            Assert.Equal(3, layout.MergedSections.Count);
            Assert.NotNull(layout.MergedSections.FirstOrDefault(s => s.Name == ".text"));
            Assert.NotNull(layout.MergedSections.FirstOrDefault(s => s.Name == ".data"));
            Assert.NotNull(layout.MergedSections.FirstOrDefault(s => s.Name == ".rodata"));
        }

        [Fact]
        public void SkipsNonAllocatableSections()
        {
            var layout = new SectionLayout();
            var sections = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 100),
                MakeSection(2, ".symtab", SHT_SYMTAB, 0, 500),
                MakeSection(3, ".strtab", SHT_STRTAB, 0, 200),
            };

            layout.AddSections(0, sections);
            layout.ComputeLayout();

            Assert.Single(layout.MergedSections);
            Assert.Equal(".text", layout.MergedSections[0].Name);
        }

        [Fact]
        public void AssignsVirtualAddresses()
        {
            var layout = new SectionLayout();
            var sections = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 100),
            };

            layout.AddSections(0, sections);
            layout.ComputeLayout();

            Assert.True(layout.MergedSections[0].VirtualAddress > 0);
            Assert.Equal(100UL, layout.MergedSections[0].TotalSize);
        }

        [Fact]
        public void RespectsAlignment()
        {
            var layout = new SectionLayout();
            var sections0 = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 17, alignment: 16),
            };
            var sections1 = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 5, alignment: 16),
            };

            layout.AddSections(0, sections0);
            layout.AddSections(1, sections1);
            layout.ComputeLayout();

            MergedSection text = layout.MergedSections[0];
            Assert.Equal(2, text.Inputs.Count);
            Assert.Equal(0UL, text.Inputs[0].OutputOffset);
            // Second input should be aligned to 16
            Assert.Equal(0UL, text.Inputs[1].OutputOffset % 16);
            Assert.True(text.Inputs[1].OutputOffset >= 17);
        }

        [Fact]
        public void GeneratesProgramHeaders()
        {
            var layout = new SectionLayout();
            var sections = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 100),
                MakeSection(2, ".data", SHT_PROGBITS, SHF_ALLOC | SHF_WRITE, 50),
            };

            layout.AddSections(0, sections);
            layout.ComputeLayout();

            Assert.Equal(2, layout.ProgramHeaders.Count);

            // .text segment should be R+X
            ProgramHeader textPh = layout.ProgramHeaders[0];
            Assert.Equal(ProgramHeader.PT_LOAD, textPh.Type);
            Assert.True((textPh.Flags & ProgramHeader.PF_X) != 0);
            Assert.True((textPh.Flags & ProgramHeader.PF_R) != 0);
            Assert.True((textPh.Flags & ProgramHeader.PF_W) == 0);

            // .data segment should be R+W
            ProgramHeader dataPh = layout.ProgramHeaders[1];
            Assert.Equal(ProgramHeader.PT_LOAD, dataPh.Type);
            Assert.True((dataPh.Flags & ProgramHeader.PF_W) != 0);
            Assert.True((dataPh.Flags & ProgramHeader.PF_R) != 0);
        }

        [Fact]
        public void GetSymbolAddress_ReturnsCorrectAddress()
        {
            var layout = new SectionLayout();
            var sections = new List<ElfSection>
            {
                MakeSection(0, null, SHT_NULL, 0, 0),
                MakeSection(1, ".text", SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, 100),
            };

            layout.AddSections(0, sections);
            layout.ComputeLayout();

            ulong addr = layout.GetSymbolAddress(0, 1, 42);
            ulong baseAddr = layout.MergedSections[0].VirtualAddress;

            Assert.Equal(baseAddr + 42, addr);
        }

        [Fact]
        public void GetSymbolAddress_ThrowsForUnknownSection()
        {
            var layout = new SectionLayout();
            layout.ComputeLayout();

            Assert.Throws<LinkerException>(() => layout.GetSymbolAddress(99, 1, 0));
        }
    }
}
