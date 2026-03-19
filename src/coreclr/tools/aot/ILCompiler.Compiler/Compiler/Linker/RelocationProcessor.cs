// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Linker
{
    /// <summary>
    /// Applies relocations from input objects to produce final executable content.
    /// </summary>
    /// <remarks>
    /// Supports x64 relocation types used by NativeAOT SDK static libraries:
    /// R_X86_64_64, R_X86_64_PC32, R_X86_64_PLT32, R_X86_64_32, R_X86_64_32S.
    /// </remarks>
    internal sealed class RelocationProcessor
    {
        private readonly SectionLayout _layout;
        private readonly SymbolResolver _resolver;

        public RelocationProcessor(SectionLayout layout, SymbolResolver resolver)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        /// <summary>
        /// Applies all relocations from an object to a mutable copy of section data.
        /// </summary>
        /// <param name="objectIndex">The index of the source object.</param>
        /// <param name="relocations">Relocations to apply.</param>
        /// <param name="symbols">Symbol table of the source object.</param>
        /// <param name="sectionBuffers">
        /// Mutable section data buffers, keyed by section index.
        /// Relocations are applied in-place.
        /// </param>
        public void Apply(
            int objectIndex,
            IReadOnlyList<ElfRelaEntry> relocations,
            IReadOnlyList<ElfSymbolEntry> symbols,
            Dictionary<int, byte[]> sectionBuffers)
        {
            foreach (ElfRelaEntry rela in relocations)
            {
                if (!sectionBuffers.TryGetValue(rela.TargetSectionIndex, out byte[] buffer))
                    continue;

                ElfSymbolEntry sym = symbols[(int)rela.SymbolIndex];
                ulong symbolAddress = ResolveSymbolAddress(objectIndex, sym);
                ulong patchOffset = rela.Offset;

                if (patchOffset >= (ulong)buffer.Length)
                    throw new LinkerException($"Relocation offset 0x{patchOffset:X} exceeds section size {buffer.Length}.");

                ulong relocAddress = _layout.GetSymbolAddress(objectIndex, rela.TargetSectionIndex, patchOffset);

                ApplyRelocation(buffer, patchOffset, rela.Type, symbolAddress, relocAddress, rela.Addend);
            }
        }

        private ulong ResolveSymbolAddress(int objectIndex, ElfSymbolEntry sym)
        {
            if (sym.IsUndefined || sym.IsGlobal || sym.IsWeak)
            {
                if (!string.IsNullOrEmpty(sym.Name) && _resolver.TryResolve(sym.Name, out ResolvedSymbol resolved))
                {
                    return _layout.GetSymbolAddress(resolved.ObjectIndex, resolved.SectionIndex, resolved.Value);
                }

                if (sym.IsUndefined)
                    throw new LinkerException($"Unresolved symbol '{sym.Name}' during relocation processing.");
            }

            // Local symbol — resolve from the same object
            return _layout.GetSymbolAddress(objectIndex, sym.SectionIndex, sym.Value);
        }

        internal static void ApplyRelocation(byte[] buffer, ulong offset, uint type,
            ulong symbolAddress, ulong relocAddress, long addend)
        {
            switch (type)
            {
                case R_X86_64_64:
                {
                    // S + A
                    ulong value = symbolAddress + (ulong)addend;
                    BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan((int)offset), value);
                    break;
                }

                case R_X86_64_PC32:
                case R_X86_64_PLT32:
                {
                    // S + A - P
                    long value = (long)symbolAddress + addend - (long)relocAddress;
                    if (value < int.MinValue || value > int.MaxValue)
                        throw new LinkerException($"PC32 relocation overflow: value 0x{value:X} at offset 0x{offset:X}.");
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan((int)offset), (int)value);
                    break;
                }

                case R_X86_64_32:
                {
                    // S + A (zero-extended)
                    ulong value = symbolAddress + (ulong)addend;
                    if (value > uint.MaxValue)
                        throw new LinkerException($"R_X86_64_32 relocation overflow: value 0x{value:X}.");
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan((int)offset), (uint)value);
                    break;
                }

                case R_X86_64_32S:
                {
                    // S + A (sign-extended)
                    long value = (long)symbolAddress + addend;
                    if (value < int.MinValue || value > int.MaxValue)
                        throw new LinkerException($"R_X86_64_32S relocation overflow: value 0x{value:X}.");
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan((int)offset), (int)value);
                    break;
                }

                case R_X86_64_NONE:
                    break;

                default:
                    throw new LinkerException($"Unsupported relocation type {type}.");
            }
        }
    }
}
