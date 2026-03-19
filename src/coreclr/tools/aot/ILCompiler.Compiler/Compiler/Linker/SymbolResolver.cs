// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace ILCompiler.Linker
{
    /// <summary>
    /// Builds a global symbol table from multiple input objects and resolves
    /// undefined references to definitions.
    /// </summary>
    internal sealed class SymbolResolver
    {
        private readonly Dictionary<string, ResolvedSymbol> _globals = new(StringComparer.Ordinal);
        private readonly List<string> _unresolved = new();

        /// <summary>All resolved global/weak symbol definitions.</summary>
        public IReadOnlyDictionary<string, ResolvedSymbol> GlobalSymbols => _globals;

        /// <summary>Symbol names that remain unresolved after all inputs are added.</summary>
        public IReadOnlyList<string> UnresolvedSymbols => _unresolved;

        /// <summary>
        /// Adds symbols from an object file to the global table.
        /// </summary>
        /// <param name="objectIndex">Index identifying the source object.</param>
        /// <param name="symbols">Symbols from the object file.</param>
        public void AddObject(int objectIndex, IReadOnlyList<ElfSymbolEntry> symbols)
        {
            AddObjectCore(objectIndex, symbols, isArchiveMember: false);
        }

        /// <summary>
        /// Adds symbols from an archive member using first-wins semantics.
        /// Duplicate strong definitions are silently ignored (matching ld behavior).
        /// </summary>
        public void AddArchiveMember(int objectIndex, IReadOnlyList<ElfSymbolEntry> symbols)
        {
            AddObjectCore(objectIndex, symbols, isArchiveMember: true);
        }

        private void AddObjectCore(int objectIndex, IReadOnlyList<ElfSymbolEntry> symbols, bool isArchiveMember)
        {
            foreach (ElfSymbolEntry sym in symbols)
            {
                if (sym.IsLocal || string.IsNullOrEmpty(sym.Name))
                    continue;

                if (sym.IsUndefined)
                    continue;

                var resolved = new ResolvedSymbol(sym.Name, objectIndex, sym.SectionIndex,
                    sym.Value, sym.Size, sym.Binding, sym.Type);

                if (_globals.TryGetValue(sym.Name, out ResolvedSymbol existing))
                {
                    // Strong symbol beats weak
                    if (existing.IsWeak && sym.IsGlobal)
                    {
                        _globals[sym.Name] = resolved;
                    }
                    else if (!existing.IsWeak && sym.IsGlobal)
                    {
                        // Archive members use first-wins (silently ignore duplicate strong)
                        if (!isArchiveMember)
                            throw new LinkerException($"Duplicate strong symbol definition: '{sym.Name}' in object {existing.ObjectIndex} and object {objectIndex}.");
                    }
                    // If existing is strong and new is weak, keep existing (do nothing)
                }
                else
                {
                    _globals[sym.Name] = resolved;
                }
            }
        }

        /// <summary>
        /// Resolves all undefined symbol references. Call after all objects have been added.
        /// </summary>
        /// <param name="allObjects">
        /// All input objects, to collect undefined references from.
        /// </param>
        public void Resolve(IReadOnlyList<IReadOnlyList<ElfSymbolEntry>> allObjects)
        {
            _unresolved.Clear();
            var undefinedSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (IReadOnlyList<ElfSymbolEntry> symbols in allObjects)
            {
                foreach (ElfSymbolEntry sym in symbols)
                {
                    if (sym.IsUndefined && !sym.IsLocal && !string.IsNullOrEmpty(sym.Name))
                    {
                        if (!_globals.ContainsKey(sym.Name))
                        {
                            undefinedSet.Add(sym.Name);
                        }
                    }
                }
            }

            _unresolved.AddRange(undefinedSet);
            _unresolved.Sort(StringComparer.Ordinal);
        }

        /// <summary>
        /// Looks up the resolved definition for a symbol name.
        /// </summary>
        public bool TryResolve(string name, out ResolvedSymbol symbol) =>
            _globals.TryGetValue(name, out symbol);
    }

    /// <summary>
    /// A symbol definition resolved to a specific object and section.
    /// </summary>
    internal sealed class ResolvedSymbol
    {
        public string Name { get; }
        public int ObjectIndex { get; }
        public ushort SectionIndex { get; }
        public ulong Value { get; }
        public ulong Size { get; }
        public byte Binding { get; }
        public byte Type { get; }

        public bool IsGlobal => Binding == ObjectWriter.ElfNative.STB_GLOBAL;
        public bool IsWeak => Binding == ObjectWriter.ElfNative.STB_WEAK;

        public ResolvedSymbol(string name, int objectIndex, ushort sectionIndex,
            ulong value, ulong size, byte binding, byte type)
        {
            Name = name;
            ObjectIndex = objectIndex;
            SectionIndex = sectionIndex;
            Value = value;
            Size = size;
            Binding = binding;
            Type = type;
        }
    }

    /// <summary>
    /// Exception thrown for linker-specific errors.
    /// </summary>
    internal sealed class LinkerException : Exception
    {
        public LinkerException(string message) : base(message) { }
    }
}
