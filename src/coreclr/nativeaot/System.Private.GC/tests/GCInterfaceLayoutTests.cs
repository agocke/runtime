// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

/// <summary>
/// Checks the C# side of the GC/EE interface against GCInterfaceOffsets.h, the table that pins
/// the layout of everything shared with the native EE.
/// </summary>
/// <remarks>
/// <para>
/// The table is checked against the C++ headers by static_asserts in
/// nativeaot/Runtime/GCInterfaceOffsetsVerify.cpp, and against the managed types by
/// <c>GCInterfaceLayout.Verify</c> at GC startup. Both of those only run in a build of the
/// NativeAOT runtime, and the second one only reports a single bool. These tests read the same
/// table directly, so a layout mistake is caught by a plain <c>dotnet test</c> run, on the
/// entry that is wrong, without building or booting a runtime.
/// </para>
/// <para>
/// They also check what neither of the other two can: that the table is <em>complete</em>. An
/// unlisted field or enumerator is not a build break anywhere else, it is simply unverified.
/// </para>
/// </remarks>
public sealed class GCInterfaceLayoutTests
{
    private const string TableResourceName = "GCInterfaceOffsets.h";

    /// <summary>
    /// Fields whose offset the table does not name, and why. Every other managed field of a type
    /// the table describes must have an entry.
    /// </summary>
    private static readonly Dictionary<string, string> s_fieldsWithoutAnEntry = new()
    {
        // The base subobject the native type inherits from FinalizerWorkItem, whose two fields
        // are pinned by that type's own entries.
        ["NoGCRegionCallbackFinalizerWorkItem.next"] = "inherited from FinalizerWorkItem",
        ["NoGCRegionCallbackFinalizerWorkItem.callback"] = "inherited from FinalizerWorkItem",
        ["TableSegment.Header"] = "the native base subobject pinned by _TableSegmentHeader",

        // C++ does not permit offsetof on a bitfield. The byte is pinned between pHandleTable
        // and bFreeList by those offsets, the packed alignment, and the total header size.
        ["_TableSegmentHeader.flags"] = "represents the native bitfield byte",

        // The native field is named dwEtwRootKind or _unused3 depending on whether the runtime is
        // built with GC_PROFILING or FEATURE_EVENT_TRACE, so the table cannot name it without
        // producing a different C# constant per build. Its offset is the same either way and is
        // pinned by the size of ScanContext together with the offset of the field before it.
        ["ScanContext.dwEtwRootKind"] = "conditionally named in the C++ header",

        // The C++ members are private, so the table cannot take their offsets. Both types have
        // pointer-sized members in a fixed declaration order, which their pinned size and
        // alignment determine between them.
        ["AffinitySet.m_bitset"] = "private in the C++ class",
        ["AffinitySet.m_bitsetDataSize"] = "private in the C++ class",
        ["GCEvent.m_impl"] = "private in the C++ class",
        ["alloc_list.head"] = "private in the C++ class",
        ["alloc_list.tail"] = "private in the C++ class",
        ["alloc_list.damage_count"] = "private in the C++ class",
#if TARGET_64BIT && !TARGET_WASM
        ["alloc_list.added_head"] = "private in the C++ class",
        ["alloc_list.added_tail"] = "private in the C++ class",
#endif

        // C# has no declaration-level alignment attribute. This unmanaged overlay exists only
        // on 32-bit targets to reproduce DECLSPEC_ALIGN(8); the native fields remain separately
        // pinned by the table.
        ["aligned_plug_and_gap._alignment"] = "forces the native 8-byte alignment on 32-bit",
    };

    /// <summary>
    /// Enumerators the table does not pin, and why.
    /// </summary>
    private static readonly Dictionary<string, string> s_enumeratorsWithoutAnEntry = new()
    {
        ["collection_mode.collection_gcstress"] = "the C++ enumerator only exists in a STRESS_HEAP build",
        ["GCEventProvider.Count"] = "not a native enumerator; the length of the per-provider arrays of gceventstatus.cpp",
    };

    /// <summary>
    /// Table constants that pin a C++ macro the port has no managed copy of, because nothing
    /// managed needs the value yet.
    /// </summary>
    private static readonly HashSet<string> s_constantsWithoutAManagedCopy = new()
    {
        "LARGE_OBJECT_SIZE",
        "min_obj_size",
        "EE_INTERFACE_MAJOR_VERSION",

        // ManagedGCEntryPoints reports these straight out of the generated table rather than
        // restating them, so there is no second managed copy to compare against.
        "GC_INTERFACE_MAJOR_VERSION",
        "GC_INTERFACE_MINOR_VERSION",

        // SoftwareWriteWatch reads this constant straight out of the generated table too, as
        // GCInterfaceOffsets.SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift, rather than
        // restating it as a private constant of its own, so there is likewise no second copy.
        "SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift",
    };

    public static IEnumerable<object[]> OffsetEntries() =>
        Table.Where(entry => entry.Kind == "GC_OFFSET")
             .Select(entry => new object[] { entry.Arguments[0], entry.Arguments[1], entry.Value });

    public static IEnumerable<object[]> SizeEntries() =>
        Table.Where(entry => entry.Kind == "GC_SIZEOF")
             .Select(entry => new object[] { entry.Arguments[0], entry.Value });

    public static IEnumerable<object[]> AlignmentEntries() =>
        Table.Where(entry => entry.Kind == "GC_ALIGNOF")
             .Select(entry => new object[] { entry.Arguments[0], entry.Value });

    public static IEnumerable<object[]> ConstantEntries() =>
        Table.Where(entry => entry.Kind is "GC_CONST" or "GC_VALUE")
             .Select(entry => new object[] { entry.Arguments[0], entry.Value });

    public static IEnumerable<object[]> TranslatedTypes() =>
        Table.Where(entry => entry.Kind == "GC_SIZEOF")
             .Select(entry => entry.Arguments[0])
             .Distinct()
             .Where(name => !FindType(name).IsEnum)
             .Select(name => new object[] { name });

    /// <summary>
    /// Enums that live in the GC namespace but are not part of the GC/EE interface: they are
    /// declared by gc/gcconfig.h, which is internal to the GC, so GCInterfaceOffsets.h has
    /// nothing to say about them and neither does the vtable/offset contract.
    /// </summary>
    private static readonly HashSet<string> s_enumsOutsideTheGCEEBoundary = new(StringComparer.Ordinal)
    {
        "HeapVerifyFlags",
        "WriteBarrierFlavor",
    };

    public static IEnumerable<object[]> TranslatedEnums() =>
        typeof(GCInterfaceLayoutTests).Assembly
            .GetTypes()
            .Where(type => type.IsEnum && type.Namespace == typeof(HandleType).Namespace)
            .Where(type => !s_enumsOutsideTheGCEEBoundary.Contains(type.Name))
            .Select(type => new object[] { type.Name });

    public static IEnumerable<object[]> Vtables() =>
        typeof(GCInterfaceLayoutTests).Assembly
            .GetTypes()
            .Where(type => type.IsValueType && type.Name.EndsWith("Vtable", StringComparison.Ordinal))
            .Select(type => new object[] { type.Name });

    [Theory]
    [MemberData(nameof(OffsetEntries))]
    public void FieldOffsetMatchesTable(string typeName, string fieldName, int expected)
    {
        Type type = FindType(typeName);

        // An array member of the C++ type becomes a run of fields with a numeric suffix, and the
        // entry pins where that run starts.
        if (type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null)
        {
            fieldName += "0";
        }

        Assert.Equal(expected, (int)Marshal.OffsetOf(type, fieldName));
    }

    [Theory]
    [MemberData(nameof(SizeEntries))]
    public void TypeSizeMatchesTable(string typeName, int expected)
    {
        Type type = FindType(typeName);
        Assert.Equal(expected, type.IsEnum ? Marshal.SizeOf(type.GetEnumUnderlyingType()) : Marshal.SizeOf(type));
    }

    [Theory]
    [MemberData(nameof(AlignmentEntries))]
    public void TypeAlignmentMatchesTable(string typeName, int expected)
    {
        Assert.Equal(expected, AlignmentOf(FindType(typeName)));
    }

    [Theory]
    [MemberData(nameof(ConstantEntries))]
    public void ConstantMatchesTable(string name, int expected)
    {
        if (s_constantsWithoutAManagedCopy.Contains(name))
        {
            return;
        }

        Assert.True(ManagedConstants.TryGetValue(name, out long actual),
            $"The table pins '{name}', but no managed enumerator or constant of that name exists. " +
            $"Either translate it or record it in {nameof(s_constantsWithoutAManagedCopy)}.");
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Every field of a translated type must be pinned. A field the table does not mention is not
    /// a build break anywhere, it is just silently unverified.
    /// </summary>
    [Theory]
    [MemberData(nameof(TranslatedTypes))]
    public void EveryFieldOfATranslatedTypeIsPinned(string typeName)
    {
        Type type = FindType(typeName);
        HashSet<string> pinned = Table
            .Where(entry => entry.Kind == "GC_OFFSET" && entry.Arguments[0] == typeName)
            .Select(entry => entry.Arguments[1])
            .ToHashSet(StringComparer.Ordinal);

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (pinned.Contains(field.Name) || s_fieldsWithoutAnEntry.ContainsKey($"{typeName}.{field.Name}"))
            {
                continue;
            }

            // An array member of the C++ type becomes a run of fields with a numeric suffix,
            // pinned through the offset of its first element.
            string withoutIndex = field.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            Assert.True(withoutIndex != field.Name && pinned.Contains(withoutIndex),
                $"{typeName}.{field.Name} has no GC_OFFSET entry in {TableResourceName}.");
        }
    }

    /// <summary>
    /// Every enumerator that crosses the GC/EE boundary must be pinned, for the same reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(TranslatedEnums))]
    public void EveryEnumeratorOfATranslatedEnumIsPinned(string typeName)
    {
        Type type = FindType(typeName);
        foreach (string name in type.GetEnumNames())
        {
            if (s_enumeratorsWithoutAnEntry.ContainsKey($"{typeName}.{name}"))
            {
                continue;
            }

            Assert.True(
                Table.Any(entry => entry.Kind is "GC_CONST" or "GC_VALUE"
                                   && (entry.Arguments[0] == name || entry.Arguments[0] == $"{typeName}_{name}")),
                $"{typeName}.{name} has no GC_CONST or GC_VALUE entry in {TableResourceName}.");
        }
    }

    /// <summary>
    /// A vtable is a run of function pointers, one per virtual slot. Their order and signatures
    /// are checked against the native headers by tools/verify-gc-interface-vtables.py; what is
    /// checked here is that the struct is exactly as wide as the slot count it declares, so that
    /// no field slips in that does not correspond to a slot.
    /// </summary>
    [Theory]
    [MemberData(nameof(Vtables))]
    public void VtableIsAsWideAsItsSlotCount(string typeName)
    {
        Type type = FindType(typeName);
        FieldInfo slotCount = type.GetField("SlotCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(slotCount);

        int slots = (int)slotCount.GetRawConstantValue();
        Assert.Equal(slots * IntPtr.Size, Marshal.SizeOf(type));

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.Equal(0, Marshal.SizeOf(field.FieldType) % IntPtr.Size);
        }
    }

    /// <summary>
    /// The interface the managed GC hands to the EE is an IGCHeapInternal, as GCHeap is in the
    /// C++ GC, so its first slots must be the IGCHeap ones the EE calls.
    /// </summary>
    [Fact]
    public void DerivedVtableStartsWithItsBaseVtable()
    {
        Type derived = FindType("IGCHeapInternalVtable");
        Assert.Equal(0, (int)Marshal.OffsetOf(derived, "IGCHeap"));
        Assert.Equal(Marshal.SizeOf(FindType("IGCHeapVtable")), (int)Marshal.OffsetOf(derived, "GetNumberOfHeaps"));
    }

    private static int AlignmentOf(Type type) =>
        (int)typeof(GCInterfaceLayoutTests)
            .GetMethod(nameof(AlignmentOfCore), BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(type)
            .Invoke(null, null);

    private static int AlignmentOfCore<T>() where T : struct
    {
        AlignProbe<T> probe = default;
        return (int)Unsafe.ByteOffset(
            ref Unsafe.As<AlignProbe<T>, byte>(ref probe),
            ref Unsafe.As<T, byte>(ref probe.Value));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AlignProbe<T> where T : struct
    {
        public byte Pad;
        public T Value;
    }

    private static Type FindType(string name) =>
        typeof(GCInterfaceLayoutTests).Assembly.GetType($"{typeof(HandleType).Namespace}.{name}", throwOnError: true);

    /// <summary>
    /// Every enumerator and every translated constant, by the name the table uses for it: the
    /// bare name of an unscoped C++ enumerator or macro, and the underscore-joined name of an
    /// enumerator of a C++ scoped enum. A bare name that two enums disagree on is dropped, so
    /// that the table entry fails to resolve rather than resolving to the wrong enum.
    /// </summary>
    private static Dictionary<string, long> ManagedConstants { get; } = BuildManagedConstants();

    private static Dictionary<string, long> BuildManagedConstants()
    {
        Dictionary<string, long> constants = new(StringComparer.Ordinal);

        foreach (Type type in typeof(GCInterfaceLayoutTests).Assembly.GetTypes())
        {
            if (type.Namespace != typeof(HandleType).Namespace)
            {
                continue;
            }

            if (type.IsEnum)
            {
                foreach (string name in type.GetEnumNames())
                {
                    long value = Convert.ToInt64(Convert.ChangeType(Enum.Parse(type, name), type.GetEnumUnderlyingType()), CultureInfo.InvariantCulture);
                    constants[$"{type.Name}_{name}"] = value;

                    if (!constants.TryAdd(name, value) && constants[name] != value)
                    {
                        constants.Remove(name);
                    }
                }
            }
            else
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    // Any integral constant, not just int: the port gives each constant the
                    // width its C++ counterpart has, and the table pins the value, not the type.
                    if (field.IsLiteral && ToInt64(field.GetRawConstantValue()) is long value)
                    {
                        constants.TryAdd(field.Name, value);
                    }
                }
            }
        }

        return constants;
    }

    /// <summary>
    /// The value of an integral constant, or null if the constant is not an integer.
    /// </summary>
    private static long? ToInt64(object value) => value switch
    {
        sbyte v => v,
        byte v => v,
        short v => v,
        ushort v => v,
        int v => v,
        uint v => v,
        long v => v,
        ulong v => unchecked((long)v),
        _ => null,
    };

    private static IReadOnlyList<Entry> Table { get; } = ReadTable();

    private static List<Entry> ReadTable()
    {
        using Stream stream = typeof(GCInterfaceLayoutTests).Assembly.GetManifestResourceStream(TableResourceName);
        Assert.NotNull(stream);
        using StreamReader reader = new(stream);

        // The table carries the value for both pointer sizes; the tests run in this process.
        int column = IntPtr.Size == 8 ? 1 : 0;
        List<Entry> entries = new();
        Stack<(bool ParentIncluded, bool Condition)> conditionals = new();
        bool includeLine = true;

        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("#if", StringComparison.Ordinal))
            {
                bool condition = line switch
                {
                    "#ifdef HOST_64BIT" => IntPtr.Size == 8,
                    "#if defined(_DEBUG) || defined(DEBUG)" =>
#if DEBUG
                        true,
#else
                        false,
#endif
                    "#if defined(FL_VERIFICATION)" => false,
                    "#if defined(TARGET_WASM)" =>
#if TARGET_WASM
                        true,
#else
                        false,
#endif
                    _ => throw new InvalidDataException($"Unknown conditional in the offsets table: '{line}'."),
                };
                conditionals.Push((includeLine, condition));
                includeLine &= condition;
                continue;
            }

            if (line == "#else")
            {
                (bool parentIncluded, bool condition) = conditionals.Pop();
                conditionals.Push((parentIncluded, !condition));
                includeLine = parentIncluded && !condition;
                continue;
            }

            if (line == "#endif")
            {
                includeLine = conditionals.Pop().ParentIncluded;
                continue;
            }

            if (!includeLine || !line.StartsWith("GC_", StringComparison.Ordinal))
            {
                continue;
            }

            int open = line.IndexOf('(');
            int close = line.LastIndexOf(')');
            Assert.True(open > 0 && close > open, $"Could not parse the table line '{line}'.");

            string kind = line[..open];
            string[] arguments = line[(open + 1)..close].Split(',').Select(argument => argument.Trim()).ToArray();
            Assert.True(arguments.Length > column + 1, $"Could not parse the table line '{line}'.");

            entries.Add(new Entry(
                kind,
                int.Parse(arguments[column], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                arguments[2..]));
        }

        Assert.NotEmpty(entries);
        return entries;
    }

    private sealed record Entry(string Kind, int Value, string[] Arguments);
}
