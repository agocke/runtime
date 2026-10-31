// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias crossgen2;

using System;
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
