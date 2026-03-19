// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Linker
{
    /// <summary>
    /// Computes the final executable layout by merging input sections,
    /// assigning virtual addresses, and generating program headers.
    /// </summary>
    internal sealed class SectionLayout
    {
        private const ulong DefaultBaseAddress = 0x400000;
        private const ulong PageSize = 0x1000;

        private readonly List<MergedSection> _mergedSections = new();
        private readonly List<ProgramHeader> _programHeaders = new();
        private ulong _currentVirtualAddress = DefaultBaseAddress;

        /// <summary>The merged output sections.</summary>
        public IReadOnlyList<MergedSection> MergedSections => _mergedSections;

        /// <summary>The program headers for the final executable.</summary>
        public IReadOnlyList<ProgramHeader> ProgramHeaders => _programHeaders;

        /// <summary>
        /// Adds input sections from an object file to the layout.
        /// Sections with the same name are merged together.
        /// </summary>
        public void AddSections(int objectIndex, IReadOnlyList<ElfSection> sections)
        {
            foreach (ElfSection section in sections)
            {
                if (!section.IsAllocatable)
                    continue;

                if (section.Type is SHT_NULL or SHT_SYMTAB or SHT_STRTAB or SHT_RELA or SHT_REL or SHT_GROUP)
                    continue;

                string name = section.Name ?? $".section{section.Index}";
                MergedSection merged = FindOrCreateMergedSection(name, section.Type, section.Flags);

                ulong alignment = Math.Max(section.Alignment, 1);
                merged.AddInput(new InputSection(objectIndex, section, alignment));
            }
        }

        /// <summary>
        /// Computes virtual addresses for all merged sections and generates
        /// program headers.
        /// </summary>
        public void ComputeLayout()
        {
            _programHeaders.Clear();
            _currentVirtualAddress = DefaultBaseAddress;

            // Reserve space for ELF header + program headers (estimated)
            _currentVirtualAddress += PageSize;

            foreach (MergedSection merged in _mergedSections)
            {
                // Align to page boundary for each new segment
                _currentVirtualAddress = Align(_currentVirtualAddress, PageSize);
                merged.VirtualAddress = _currentVirtualAddress;

                ulong sectionOffset = 0;
                foreach (InputSection input in merged.Inputs)
                {
                    sectionOffset = Align(sectionOffset, input.Alignment);
                    input.OutputOffset = sectionOffset;
                    sectionOffset += input.Section.Size;
                }

                merged.TotalSize = sectionOffset;
                _currentVirtualAddress += sectionOffset;

                // Create a PT_LOAD program header for each merged section
                uint flags = ProgramHeader.PF_R;
                if ((merged.Flags & SHF_WRITE) != 0)
                    flags |= ProgramHeader.PF_W;
                if ((merged.Flags & SHF_EXECINSTR) != 0)
                    flags |= ProgramHeader.PF_X;

                _programHeaders.Add(new ProgramHeader(
                    type: ProgramHeader.PT_LOAD,
                    flags: flags,
                    virtualAddress: merged.VirtualAddress,
                    size: merged.TotalSize,
                    alignment: PageSize));
            }
        }

        /// <summary>
        /// Gets the final virtual address of a symbol given its object, section,
        /// and value within the section.
        /// </summary>
        public ulong GetSymbolAddress(int objectIndex, int sectionIndex, ulong valueInSection)
        {
            foreach (MergedSection merged in _mergedSections)
            {
                foreach (InputSection input in merged.Inputs)
                {
                    if (input.ObjectIndex == objectIndex && input.Section.Index == sectionIndex)
                    {
                        return merged.VirtualAddress + input.OutputOffset + valueInSection;
                    }
                }
            }

            throw new LinkerException($"Cannot resolve address for object {objectIndex}, section {sectionIndex}.");
        }

        private MergedSection FindOrCreateMergedSection(string name, uint type, ulong flags)
        {
            foreach (MergedSection existing in _mergedSections)
            {
                if (existing.Name == name)
                    return existing;
            }

            var merged = new MergedSection(name, type, flags);
            _mergedSections.Add(merged);

            return merged;
        }

        private static ulong Align(ulong value, ulong alignment) =>
            alignment <= 1 ? value : (value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>
    /// A merged output section composed of input sections with the same name.
    /// </summary>
    internal sealed class MergedSection
    {
        private readonly List<InputSection> _inputs = new();

        public string Name { get; }
        public uint Type { get; }
        public ulong Flags { get; }
        public ulong VirtualAddress { get; set; }
        public ulong TotalSize { get; set; }

        public IReadOnlyList<InputSection> Inputs => _inputs;

        public MergedSection(string name, uint type, ulong flags)
        {
            Name = name;
            Type = type;
            Flags = flags;
        }

        public void AddInput(InputSection input) => _inputs.Add(input);
    }

    /// <summary>
    /// An input section from a specific object file, placed at a computed offset
    /// within a merged output section.
    /// </summary>
    internal sealed class InputSection
    {
        public int ObjectIndex { get; }
        public ElfSection Section { get; }
        public ulong Alignment { get; }
        public ulong OutputOffset { get; set; }

        public InputSection(int objectIndex, ElfSection section, ulong alignment)
        {
            ObjectIndex = objectIndex;
            Section = section;
            Alignment = alignment;
        }
    }

    /// <summary>
    /// ELF program header entry.
    /// </summary>
    internal sealed class ProgramHeader
    {
        public const uint PT_NULL = 0;
        public const uint PT_LOAD = 1;
        public const uint PT_DYNAMIC = 2;
        public const uint PT_INTERP = 3;
        public const uint PT_NOTE = 4;
        public const uint PT_PHDR = 6;
        public const uint PT_GNU_STACK = 0x6474E551;

        public const uint PF_X = 1;
        public const uint PF_W = 2;
        public const uint PF_R = 4;

        public uint Type { get; }
        public uint Flags { get; }
        public ulong VirtualAddress { get; }
        public ulong Size { get; }
        public ulong Alignment { get; }

        public ProgramHeader(uint type, uint flags, ulong virtualAddress, ulong size, ulong alignment)
        {
            Type = type;
            Flags = flags;
            VirtualAddress = virtualAddress;
            Size = size;
            Alignment = alignment;
        }
    }
}
