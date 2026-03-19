// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using ILCompiler.Linker;
using Xunit;

namespace ILCompiler.Compiler.Tests
{
    public class SymbolResolverTests
    {
        private static List<ElfSymbolEntry> MakeSymbols(params ElfSymbolEntry[] entries) => new(entries);

        private static ElfSymbolEntry Global(string name, ushort section = 1, ulong value = 0) =>
            new(name, value, 0, section, binding: 1, type: 2, visibility: 0);

        private static ElfSymbolEntry Weak(string name, ushort section = 1, ulong value = 0) =>
            new(name, value, 0, section, binding: 2, type: 2, visibility: 0);

        private static ElfSymbolEntry Undefined(string name) =>
            new(name, 0, 0, sectionIndex: 0, binding: 1, type: 0, visibility: 0);

        private static ElfSymbolEntry Local(string name, ushort section = 1) =>
            new(name, 0, 0, section, binding: 0, type: 0, visibility: 0);

        [Fact]
        public void ResolvesGlobalSymbol()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols(Global("main")));

            Assert.True(resolver.TryResolve("main", out ResolvedSymbol sym));
            Assert.Equal(0, sym.ObjectIndex);
            Assert.True(sym.IsGlobal);
        }

        [Fact]
        public void StrongBeatsWeak()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols(Weak("foo")));
            resolver.AddObject(1, MakeSymbols(Global("foo")));

            Assert.True(resolver.TryResolve("foo", out ResolvedSymbol sym));
            Assert.Equal(1, sym.ObjectIndex);
            Assert.True(sym.IsGlobal);
        }

        [Fact]
        public void WeakDoesNotOverrideStrong()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols(Global("foo")));
            resolver.AddObject(1, MakeSymbols(Weak("foo")));

            Assert.True(resolver.TryResolve("foo", out ResolvedSymbol sym));
            Assert.Equal(0, sym.ObjectIndex);
        }

        [Fact]
        public void DuplicateStrongSymbolThrows()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols(Global("dup")));

            Assert.Throws<LinkerException>(() =>
                resolver.AddObject(1, MakeSymbols(Global("dup"))));
        }

        [Fact]
        public void MultipleWeakSymbolsKeepsFirst()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols(Weak("wk", value: 100)));
            resolver.AddObject(1, MakeSymbols(Weak("wk", value: 200)));

            Assert.True(resolver.TryResolve("wk", out ResolvedSymbol sym));
            Assert.Equal(0, sym.ObjectIndex);
            Assert.Equal(100UL, sym.Value);
        }

        [Fact]
        public void IgnoresLocalSymbols()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols(Local("internal_fn")));

            Assert.False(resolver.TryResolve("internal_fn", out _));
        }

        [Fact]
        public void DetectsUnresolvedSymbols()
        {
            var resolver = new SymbolResolver();
            var obj0 = MakeSymbols(Global("defined"), Undefined("missing"));
            resolver.AddObject(0, obj0);
            resolver.Resolve(new[] { obj0 });

            Assert.Single(resolver.UnresolvedSymbols);
            Assert.Equal("missing", resolver.UnresolvedSymbols[0]);
        }

        [Fact]
        public void ResolvedSymbolIsNotUnresolved()
        {
            var resolver = new SymbolResolver();
            var obj0 = MakeSymbols(Global("foo"), Undefined("bar"));
            var obj1 = MakeSymbols(Global("bar"));
            resolver.AddObject(0, obj0);
            resolver.AddObject(1, obj1);
            resolver.Resolve(new[] { obj0, obj1 });

            Assert.Empty(resolver.UnresolvedSymbols);
        }

        [Fact]
        public void EmptyObjectProducesNoSymbols()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols());

            Assert.Empty(resolver.GlobalSymbols);
        }

        [Fact]
        public void MultipleObjectsResolveCorrectly()
        {
            var resolver = new SymbolResolver();
            resolver.AddObject(0, MakeSymbols(Global("a"), Global("b")));
            resolver.AddObject(1, MakeSymbols(Global("c"), Global("d")));

            Assert.True(resolver.TryResolve("a", out var sa) && sa.ObjectIndex == 0);
            Assert.True(resolver.TryResolve("b", out var sb) && sb.ObjectIndex == 0);
            Assert.True(resolver.TryResolve("c", out var sc) && sc.ObjectIndex == 1);
            Assert.True(resolver.TryResolve("d", out var sd) && sd.ObjectIndex == 1);
        }
    }
}
