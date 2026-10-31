// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias crossgen2;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using crossgen2::ILCompiler.DependencyAnalysis;
using crossgen2::ILCompiler.ObjectWriter;
using crossgen2::Internal.Text;
using Writer = crossgen2::ILCompiler.ObjectWriter.ObjectWriter;

using Xunit;

namespace ILCompiler.ReadyToRun.Tests;

public class ObjectWriterTests
{
    [Theory]
    [InlineData(10, false)]
    [InlineData(8192, false)]
    [InlineData(8192, true)]
    public void SuffixSortingPreservesContentOffsetsAndIncrementalFlush(int count, bool embeddedNull)
    {
        List<Utf8String> names = [new(""), new("a"), new("ba"), new("cba"), new("ba"),
            new("\u03b1\u03b2"), new("b"), new("ab"), new("::End"), new("::Suffix")];
        for (int i = 0; i < count; i++)
            names.Add(new Utf8String(FormattableString.Invariant($"prefix{i:D6}") + (i % 3 == 0 ? "::End" : "::Suffix")));
        if (embeddedNull)
            names.Add(new Utf8String("prefix\0suffix"));

        (byte[] bytes, uint[] offsets) = Build(reverse: false);
        (byte[] reversedBytes, uint[] reversedOffsets) = Build(reverse: true);
        Assert.Equal(bytes, reversedBytes);
        Assert.Equal(offsets, reversedOffsets);
        for (int i = 0; i < names.Count; i++)
        {
            ReadOnlySpan<byte> name = names[i].AsSpan();
            Assert.True(name.SequenceEqual(bytes.AsSpan((int)offsets[i], name.Length)));
            Assert.Equal(0, bytes[(int)offsets[i] + name.Length]);
        }
        Assert.Equal(offsets[2], offsets[4]);
        Assert.Equal(offsets[3] + 1, offsets[2]);
        Assert.Equal(offsets[2] + 1, offsets[1]);

        (byte[] Bytes, uint[] Offsets) Build(bool reverse)
        {
            StringTableBuilder table = new();
            for (int i = 0; i < names.Count; i++)
                table.ReserveString(names[reverse ? names.Count - i - 1 : i]);
            uint firstSize = table.Size;
            table.ReserveString(new Utf8String("second-flush::Suffix"));
            uint[] result = new uint[names.Count];
            for (int i = 0; i < names.Count; i++)
                result[i] = table.GetStringOffset(names[i]);
            using MemoryStream output = new();
            table.Write(output);
            Assert.Equal(table.Size, (uint)output.Length);
            Assert.True(output.Length > firstSize);
            byte[] data = output.ToArray();
            uint secondOffset = table.GetStringOffset(new Utf8String("second-flush::Suffix"));
            Assert.True("second-flush::Suffix\0"u8.SequenceEqual(data.AsSpan((int)secondOffset)));

            return (data, result);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(4097)]
    public void RelocationSlabsPreserveFieldsAcrossBoundariesAndReverse(int count)
    {
        Type listType = typeof(Writer).GetNestedType("RelocationList", BindingFlags.NonPublic)!;
        Type entryType = typeof(Writer).GetNestedType("SymbolicRelocation", BindingFlags.NonPublic)!;
        object list = Activator.CreateInstance(listType, nonPublic: true)!;
        MethodInfo add = listType.GetMethod("Add")!;
        for (int i = 0; i < count; i++)
        {
            object entry = Activator.CreateInstance(entryType,
                (long)i, RelocType.IMAGE_REL_BASED_DIR64, new Utf8String("Symbol"), -(long)i)!;
            add.Invoke(list, [entry]);
        }
        Assert.Equal(count, listType.GetProperty("Count")!.GetValue(list));
        Check(reverse: false);
        listType.GetMethod("Reverse")!.Invoke(list, null);
        Check(reverse: true);
        listType.GetMethod("Reverse")!.Invoke(list, null);
        Check(reverse: false);

        void Check(bool reverse)
        {
            object enumerator = listType.GetMethod("GetEnumerator")!.Invoke(list, null)!;
            MethodInfo next = enumerator.GetType().GetMethod("MoveNext")!;
            PropertyInfo current = enumerator.GetType().GetProperty("Current")!;
            int seen = 0;
            while ((bool)next.Invoke(enumerator, null)!)
            {
                object entry = current.GetValue(enumerator)!;
                long expected = reverse ? count - seen - 1 : seen;
                Assert.Equal(expected, entryType.GetField("Offset")!.GetValue(entry));
                Assert.Equal(-expected, entryType.GetField("Addend")!.GetValue(entry));
                Assert.Equal(RelocType.IMAGE_REL_BASED_DIR64, entryType.GetField("Type")!.GetValue(entry));
                Assert.Equal(new Utf8String("Symbol"), entryType.GetField("SymbolName")!.GetValue(entry));
                seen++;
            }
            Assert.Equal(count, seen);
        }
    }
}
