// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using ILCompiler.Linker;
using Xunit;

namespace ILCompiler.Compiler.Tests
{
    public class ElfArchiveReaderTests
    {
        [Fact]
        public void ParsesMinimalArchive()
        {
            byte[] member1 = new byte[] { 0xCC, 0xCC, 0xCC, 0xCC };
            byte[] archive = ElfTestData.BuildArchive(("hello.o", member1));

            var reader = new ElfArchiveReader(archive);

            Assert.Single(reader.Members);
            Assert.Equal("hello.o", reader.Members[0].Name);
            Assert.Equal(4, reader.Members[0].DataSize);
        }

        [Fact]
        public void ParsesMultipleMembers()
        {
            byte[] obj1 = new byte[] { 1, 2, 3 };
            byte[] obj2 = new byte[] { 4, 5, 6, 7, 8 };
            byte[] obj3 = new byte[] { 9 };
            byte[] archive = ElfTestData.BuildArchive(
                ("a.o", obj1),
                ("b.o", obj2),
                ("c.o", obj3));

            var reader = new ElfArchiveReader(archive);

            Assert.Equal(3, reader.Members.Count);
            Assert.Equal("a.o", reader.Members[0].Name);
            Assert.Equal("b.o", reader.Members[1].Name);
            Assert.Equal("c.o", reader.Members[2].Name);
            Assert.Equal(3, reader.Members[0].DataSize);
            Assert.Equal(5, reader.Members[1].DataSize);
            Assert.Equal(1, reader.Members[2].DataSize);
        }

        [Fact]
        public void ExtractsMemberData()
        {
            byte[] expected = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            byte[] archive = ElfTestData.BuildArchive(("test.o", expected));

            var reader = new ElfArchiveReader(archive);
            ReadOnlySpan<byte> actual = reader.GetMemberData(reader.Members[0]);

            Assert.True(actual.SequenceEqual(expected));
        }

        [Fact]
        public void ParsesSymbolTable()
        {
            byte[] obj1 = new byte[] { 1 };
            byte[] obj2 = new byte[] { 2 };
            byte[] archive = ElfTestData.BuildArchive(
                new[] { ("a.o", obj1), ("b.o", obj2) },
                new[] { ("foo", 0), ("bar", 1), ("baz", 0) });

            var reader = new ElfArchiveReader(archive);

            Assert.Equal(2, reader.Members.Count);
            Assert.True(reader.SymbolTable.ContainsKey("foo"));
            Assert.True(reader.SymbolTable.ContainsKey("bar"));
            Assert.True(reader.SymbolTable.ContainsKey("baz"));
            Assert.Contains(0, reader.SymbolTable["foo"]);
            Assert.Contains(1, reader.SymbolTable["bar"]);
            Assert.Contains(0, reader.SymbolTable["baz"]);
        }

        [Fact]
        public void SelectMembersForSymbols_ReturnsCorrectSubset()
        {
            byte[] obj1 = new byte[] { 1 };
            byte[] obj2 = new byte[] { 2 };
            byte[] obj3 = new byte[] { 3 };
            byte[] archive = ElfTestData.BuildArchive(
                new[] { ("a.o", obj1), ("b.o", obj2), ("c.o", obj3) },
                new[] { ("alpha", 0), ("beta", 1), ("gamma", 2) });

            var reader = new ElfArchiveReader(archive);
            var selected = reader.SelectMembersForSymbols(new[] { "beta", "gamma" });

            Assert.Equal(2, selected.Count);
            Assert.Contains(1, selected);
            Assert.Contains(2, selected);
            Assert.DoesNotContain(0, selected);
        }

        [Fact]
        public void SelectMembersForSymbols_UnknownSymbolReturnsEmpty()
        {
            byte[] obj1 = new byte[] { 1 };
            byte[] archive = ElfTestData.BuildArchive(
                new[] { ("a.o", obj1) },
                new[] { ("known", 0) });

            var reader = new ElfArchiveReader(archive);
            var selected = reader.SelectMembersForSymbols(new[] { "unknown" });

            Assert.Empty(selected);
        }

        [Fact]
        public void RejectsInvalidMagic()
        {
            byte[] badData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

            Assert.Throws<InvalidDataException>(() => new ElfArchiveReader(badData));
        }

        [Fact]
        public void RejectsTruncatedFile()
        {
            Assert.Throws<InvalidDataException>(() => new ElfArchiveReader(new byte[] { 0x21 }));
        }

        [Fact]
        public void HandlesOddSizedMembers()
        {
            // Odd-sized member data should be padded to 2-byte boundary in the archive
            byte[] obj1 = new byte[] { 1, 2, 3 }; // 3 bytes (odd)
            byte[] obj2 = new byte[] { 4, 5, 6, 7 }; // 4 bytes (even)
            byte[] archive = ElfTestData.BuildArchive(("odd.o", obj1), ("even.o", obj2));

            var reader = new ElfArchiveReader(archive);

            Assert.Equal(2, reader.Members.Count);
            Assert.Equal(3, reader.Members[0].DataSize);
            Assert.Equal(4, reader.Members[1].DataSize);

            // Both should have correct data
            Assert.True(reader.GetMemberData(reader.Members[0]).SequenceEqual(obj1));
            Assert.True(reader.GetMemberData(reader.Members[1]).SequenceEqual(obj2));
        }
    }
}
