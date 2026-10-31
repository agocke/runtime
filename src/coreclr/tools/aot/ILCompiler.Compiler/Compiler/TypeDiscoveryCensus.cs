// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysisFramework;
using Internal.TypeSystem;
using DependencySink = ILCompiler.DependencyAnalysisFramework.DependencyNodeCore<ILCompiler.DependencyAnalysis.NodeFactory>.DependencySink;
using Entry = ILCompiler.DependencyAnalysisFramework.DependencyNodeCore<ILCompiler.DependencyAnalysis.NodeFactory>.DependencySink.DiagnosticEntry;

namespace ILCompiler;

public static class TypeDiscoveryCensus
{
    internal enum Unit
    {
        ClassSlots,
        ClassResolutions,
        ClassImplementations,
        Interfaces,
        InterfaceSlots,
        InterfaceResolutions,
        StaticInterfaceResolutions,
        DefaultResolutions,
        DefaultImplementations,
        VariantImplementations,
        OverrideChecks,
        FieldsExamined,
        FieldsRetained,
        MethodsExamined,
        MethodsRetained,
        GenericConstraintQueries,
        Canonicalizations,
        InstantiationMappings,
        FactoryQueries,
        SlotComparisons,
        VTableCreations,
        AttributeQueries,
        RequiresScopeQueries,
        PropertySearches,
        PropertySteps,
        EventSearches,
        EventSteps,
        BlockedTypeVisits,
        BlockedMemberQueries,
        Count,
    }

    internal enum Section { Metadata, Members, Constraints, ClassSlots, InterfaceSlots, Notes, Overrides, Count }

    private static readonly Dictionary<NodeFactory, Stage> s_stages = new(ReferenceEqualityComparer.Instance);
    [ThreadStatic]
    private static Record s_current;
    [ThreadStatic]
    private static Record s_scratch;
    [ThreadStatic]
    private static bool s_replaying;

    internal static bool Enabled { get; } =
        AppContext.TryGetSwitch("ILCompiler.TypeDiscoveryCensus", out bool enabled) && enabled;

    internal static Scope Start(EETypeNode node, NodeFactory factory, DependencySink sink)
    {
        if (!Enabled || s_replaying || node is not ConstructedEETypeNode)
            return default;

        Debug.Assert(s_current is null);
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Type discovery CPU census requires 64-bit Linux.");
        if (!s_stages.TryGetValue(factory, out Stage stage))
        {
            stage = new Stage(factory.GetType().Name + "/" + factory.MetadataManager.GetType().Name);
            s_stages.Add(factory, stage);
        }

        Record record = s_scratch ??= new Record();
        Array.Clear(record.Units);
        Array.Clear(record.SectionCpu);
        Array.Clear(record.SectionCalls);
        record.Notes.Clear();
        record.Overrides.Clear();
        record.Node = node;
        record.Factory = factory;
        record.Sink = sink;
        record.StartCounts = sink.GetDiagnosticCounts();
        record.Stage = stage;
        record.StartAllocated = GC.GetAllocatedBytesForCurrentThread();
        s_current = record;
        return new Scope(CompilationEventSource.ReadThreadCpuNanoseconds());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Count(Unit unit, int count = 1)
    {
        if (Enabled && s_current is Record record)
            record.Units[(int)unit] += count;
    }

    private sealed class Record
    {
        public readonly long[] Units = new long[(int)Unit.Count];
        public readonly long[] SectionCpu = new long[(int)Section.Count];
        public readonly long[] SectionCalls = new long[(int)Section.Count];
        public readonly List<OverridePair> Notes = new();
        public readonly List<OverridePair> Overrides = new();
        public EETypeNode Node;
        public NodeFactory Factory;
        public DependencySink Sink;
        public (int Unconditional, int Conditional) StartCounts;
        public Stage Stage;
        public long StartAllocated;
    }

    private sealed class Aggregate
    {
        public long Calls;
        public long Cpu;
        public long Allocated;
        public long Edges;
        public readonly long[] Units = new long[(int)Unit.Count];
        public readonly long[] SectionCpu = new long[(int)Section.Count];
        public readonly long[] SectionCalls = new long[(int)Section.Count];

        public void Add(Record record, long cpu, long allocated, long edges)
        {
            Calls++;
            Cpu += cpu;
            Allocated += allocated;
            Edges += edges;
            for (int i = 0; i < Units.Length; i++)
                Units[i] += record.Units[i];
            for (int i = 0; i < SectionCpu.Length; i++)
            {
                SectionCpu[i] += record.SectionCpu[i];
                SectionCalls[i] += record.SectionCalls[i];
            }
        }
    }

    private sealed class Stage
    {
        public readonly string Name;
        public readonly Dictionary<TypeDesc, Aggregate> Families = new(ReferenceEqualityComparer.Instance);
        public readonly SortedDictionary<int, Bucket> Buckets = new();
        public readonly Random Random = new(84317);
        public readonly Aggregate Total = new();
        public readonly HashSet<(MethodDesc Declaration, MethodDesc Implementation)> ValidationPairs = new(new ValidationPairComparer());
        public long NonSelfValidations;

        public Stage(string name) => Name = name;
    }

    private sealed class ValidationPairComparer : IEqualityComparer<(MethodDesc Declaration, MethodDesc Implementation)>
    {
        public bool Equals((MethodDesc Declaration, MethodDesc Implementation) x,
            (MethodDesc Declaration, MethodDesc Implementation) y) =>
            ReferenceEquals(x.Declaration, y.Declaration) && ReferenceEquals(x.Implementation, y.Implementation);

        public int GetHashCode((MethodDesc Declaration, MethodDesc Implementation) pair) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Declaration), RuntimeHelpers.GetHashCode(pair.Implementation));
    }

    private sealed class Bucket
    {
        public readonly string Name;
        public readonly Aggregate Total = new();
        public readonly List<Case> Cases = new();

        public Bucket(string name) => Name = name;

        public override string ToString() => Name;
    }

    private sealed class Case
    {
        public EETypeNode Node;
        public NodeFactory Factory;
        public Entry[] Expected;
        public long[] Units;
        public long OriginalCpu;
        public long OriginalAllocated;
        public MethodDesc[] QuerySlots;
        public MethodDesc[] CanonQuerySlots;
        public MethodDesc[] ReflectedMethods;
        public FieldDesc[] ReflectedFields;
        public int MemberCanonicalizations;
        public OverridePair[] Notes;
        public OverridePair[] Overrides;
        public MethodDesc[] ConstraintMethods;
        public MethodDesc[] MemberPolicyMethods;
        public FieldDesc[] MemberPolicyFields;
        public readonly DependencySink Sink = new();
    }

    private readonly struct OverridePair(MethodDesc declaration, MethodDesc implementation, TypeSystemEntity origin)
    {
        public readonly MethodDesc Declaration = declaration;
        public readonly MethodDesc Implementation = implementation;
        public readonly TypeSystemEntity Origin = origin;
    }

    internal static Timing Time(Section section)
    {
        if (!Enabled || s_current is null)
            return default;
        if ((section is Section.Notes or Section.Overrides)
            && s_current.Factory.MetadataManager is not UsageBasedMetadataManager)
            return default;
        return new Timing(section, CompilationEventSource.ReadThreadCpuNanoseconds());
    }

    internal static Timing TimeNote(MethodDesc declaration, MethodDesc implementation, TypeSystemEntity origin = null)
    {
        if (Enabled && s_current is Record record)
        {
            record.Notes.Add(new OverridePair(declaration, implementation, origin));
            if (record.Factory.MetadataManager is UsageBasedMetadataManager)
            {
                MethodDesc typicalDeclaration = declaration.GetTypicalMethodDefinition();
                MethodDesc typicalImplementation = implementation.GetTypicalMethodDefinition();
                if (typicalDeclaration != typicalImplementation)
                {
                    record.Stage.NonSelfValidations++;
                    record.Stage.ValidationPairs.Add((typicalDeclaration, typicalImplementation));
                }
            }
        }
        return Time(Section.Notes);
    }

    internal static Timing TimeOverride(MethodDesc declaration, MethodDesc implementation)
    {
        if (Enabled && s_current is Record record)
            record.Overrides.Add(new OverridePair(declaration, implementation, null));
        return Time(Section.Overrides);
    }

    internal readonly struct Timing : IDisposable
    {
        private readonly Record _record;
        private readonly Section _section;
        private readonly long _start;

        internal Timing(Section section, long start)
        {
            _record = s_current;
            _section = section;
            _start = start;
        }

        public void Dispose()
        {
            if (_record is not null)
            {
                _record.SectionCpu[(int)_section] += CompilationEventSource.ReadThreadCpuNanoseconds() - _start;
                _record.SectionCalls[(int)_section]++;
            }
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Record _record;
        private readonly long _start;

        internal Scope(long start)
        {
            _record = s_current;
            _start = start;
        }

        public void Dispose()
        {
            if (_record is null)
                return;

            long cpu = CompilationEventSource.ReadThreadCpuNanoseconds() - _start;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - _record.StartAllocated;
            Record record = _record;
            s_current = null;
            var end = record.Sink.GetDiagnosticCounts();
            int edges = end.Unconditional - record.StartCounts.Unconditional + end.Conditional - record.StartCounts.Conditional;
            Stage stage = record.Stage;
            stage.Total.Add(record, cpu, allocated, edges);
            TypeDesc definition = record.Node.Type.GetTypeDefinition();
            if (!stage.Families.TryGetValue(definition, out Aggregate family))
            {
                family = new Aggregate();
                stage.Families.Add(definition, family);
            }
            family.Add(record, cpu, allocated, edges);

            long work = record.Units[(int)Unit.ClassSlots] + record.Units[(int)Unit.InterfaceSlots]
                + record.Units[(int)Unit.FieldsExamined] + record.Units[(int)Unit.MethodsExamined];
            int bin = BitOperations.Log2((ulong)Math.Max(work, 1));
            if (!stage.Buckets.TryGetValue(bin, out Bucket bucket))
            {
                bucket = new Bucket(stage.Name + "/work-log2-" + bin);
                stage.Buckets.Add(bin, bucket);
            }
            bucket.Total.Add(record, cpu, allocated, edges);
            const int ReservoirSize = 32;
            long selected = bucket.Total.Calls <= ReservoirSize ? bucket.Total.Calls - 1
                : stage.Random.NextInt64(bucket.Total.Calls);
            if (selected < ReservoirSize)
            {
                var sample = new Case
                {
                    Node = record.Node,
                    Factory = record.Factory,
                    Expected = record.Sink.GetDiagnosticSnapshot(record.StartCounts.Unconditional, record.StartCounts.Conditional),
                    Units = (long[])record.Units.Clone(),
                    OriginalCpu = cpu,
                    OriginalAllocated = allocated,
                    Notes = record.Notes.ToArray(),
                    Overrides = record.Overrides.ToArray(),
                };
                if (selected < bucket.Cases.Count)
                    bucket.Cases[(int)selected] = sample;
                else
                    bucket.Cases.Add(sample);
            }
        }
    }

    public static object[] GetBenchmarkCases()
    {
        var result = new List<object>();
        foreach (Stage stage in s_stages.Values)
        {
            foreach (Bucket bucket in stage.Buckets.Values)
            {
                foreach (Case sample in bucket.Cases)
                    PreparePrimitives(sample);
                result.Add(bucket);
            }
        }

        return result.ToArray();
    }

    private static void PreparePrimitives(Case sample)
    {
        if (sample.QuerySlots is not null)
            return;

        DefType type = sample.Node.Type.GetClosestDefType();
        var slots = new List<MethodDesc>();
        if (sample.Units[(int)Unit.ClassSlots] != 0)
        {
            foreach (MethodDesc method in type.EnumAllVirtualSlots())
                if (!method.HasInstantiation)
                    slots.Add(method);
        }
        if (sample.Units[(int)Unit.InterfaceSlots] != 0)
        {
            bool needsInstanceMethods = !type.IsInterface || ((MetadataType)type).IsDynamicInterfaceCastableImplementation();
            foreach (DefType iface in type.RuntimeInterfaces)
                foreach (MethodDesc method in iface.EnumAllVirtualSlots())
                    if (!method.HasInstantiation && (method.Signature.IsStatic || needsInstanceMethods))
                        slots.Add(method);
        }
        sample.QuerySlots = slots.ToArray();
        sample.CanonQuerySlots = new MethodDesc[slots.Count];
        for (int i = 0; i < slots.Count; i++)
            sample.CanonQuerySlots[i] = slots[i].GetCanonMethodTarget(CanonicalFormKind.Specific);

        var fields = new List<FieldDesc>();
        var policyFields = new List<FieldDesc>();
        if (sample.Units[(int)Unit.FieldsExamined] != 0)
        {
            foreach (FieldDesc field in sample.Node.Type.GetFields())
            {
                if (!field.IsLiteral)
                    policyFields.Add(field);
                if (!field.IsLiteral && !sample.Factory.MetadataManager.IsReflectionBlocked(field))
                {
                    fields.Add(field);
                    fields.Add(field.GetTypicalFieldDefinition());
                }
            }
        }
        sample.ReflectedFields = fields.ToArray();
        sample.MemberPolicyFields = policyFields.ToArray();
        var methods = new List<MethodDesc>();
        var constraints = new List<MethodDesc>();
        var policyMethods = new List<MethodDesc>();
        if (sample.Units[(int)Unit.MethodsExamined] != 0)
        {
            foreach (MethodDesc method in sample.Node.Type.GetMethods())
            {
                policyMethods.Add(method);
                if (method.HasInstantiation && !sample.Factory.MetadataManager.IsReflectionBlocked(method))
                    constraints.Add(method);
                if (!method.HasInstantiation)
                {
                    sample.MemberCanonicalizations++;
                    if (!sample.Factory.MetadataManager.IsReflectionBlocked(method))
                    {
                        methods.Add(method.GetCanonMethodTarget(CanonicalFormKind.Specific));
                        methods.Add(method.GetTypicalMethodDefinition());
                    }
                }
            }
        }
        sample.ReflectedMethods = methods.ToArray();
        sample.ConstraintMethods = constraints.ToArray();
        sample.MemberPolicyMethods = policyMethods.ToArray();
    }

    public static void ValidateReplayCases()
    {
        s_replaying = true;
        try
        {
            foreach (Stage stage in s_stages.Values)
            {
                foreach (Bucket bucket in stage.Buckets.Values)
                {
                    foreach (Case sample in bucket.Cases)
                    {
                        sample.Node.AddConditionalDependencies(sample.Sink, sample.Factory);
                        int index = 0;
                        using var entries = sample.Sink.Drain();
                        while (entries.MoveNext())
                        {
                            if (index >= sample.Expected.Length
                                || !ReferenceEquals(entries.Dependency, sample.Expected[index].Node)
                                || !ReferenceEquals(entries.OtherReasonNode, sample.Expected[index].OtherReasonNode)
                                || entries.Reason != sample.Expected[index].Reason)
                                throw new InvalidOperationException("Type discovery replay changed dependencies: " + sample.Node.Type);
                            index++;
                        }
                        if (index != sample.Expected.Length)
                            throw new InvalidOperationException("Type discovery replay changed dependency count: " + sample.Node.Type);
                    }
                }
            }
        }
        finally
        {
            s_replaying = false;
        }
    }

    public static long RunBenchmark(object value, int operation)
    {
        var bucket = (Bucket)value;
        s_replaying = true;
        long result = 0;
        try
        {
            foreach (Case sample in bucket.Cases)
            {
                TypeDesc type = sample.Node.Type;
                DefType defType = type.GetClosestDefType();
                switch (operation)
                {
                    case 0:
                        sample.Node.AddConditionalDependencies(sample.Sink, sample.Factory);
                        using (var entries = sample.Sink.Drain())
                            while (entries.MoveNext())
                                result++;
                        break;
                    case 1:
                        if (sample.Units[(int)Unit.ClassSlots] != 0)
                            foreach (MethodDesc slot in defType.EnumAllVirtualSlots())
                                result += slot.HasInstantiation ? 2 : 1;
                        break;
                    case 2:
                        if (sample.Units[(int)Unit.ClassSlots] != 0)
                        {
                            foreach (MethodDesc slot in defType.EnumAllVirtualSlots())
                                if (!slot.HasInstantiation)
                                    result += RuntimeHelpers.GetHashCode(defType.FindVirtualFunctionTargetMethodOnObjectType(slot));
                        }
                        break;
                    case 3:
                        if (sample.Units[(int)Unit.InterfaceSlots] == 0)
                            break;
                        bool needsInstanceMethods = !defType.IsInterface
                            || ((MetadataType)defType).IsDynamicInterfaceCastableImplementation();
                        var definition = (DefType)defType.GetTypeDefinition();
                        DefType[] interfaces = defType.RuntimeInterfaces;
                        DefType[] definitionInterfaces = definition.RuntimeInterfaces;
                        for (int i = 0; i < interfaces.Length; i++)
                        {
                            foreach (MethodDesc method in interfaces[i].EnumAllVirtualSlots())
                            {
                                if (method.HasInstantiation)
                                    continue;
                                if (!method.Signature.IsStatic && !needsInstanceMethods)
                                    continue;
                                MethodDesc query = interfaces[i] == definitionInterfaces[i] ? method :
                                    sample.Factory.TypeSystemContext.GetMethodForInstantiatedType(
                                        method.GetTypicalMethodDefinition(), (InstantiatedType)definitionInterfaces[i]);
                                MethodDesc target = query.Signature.IsStatic
                                    ? definition.ResolveInterfaceMethodToStaticVirtualMethodOnType(query)
                                    : definition.ResolveInterfaceMethodToVirtualMethodOnType(query);
                                if (target is null)
                                    definition.ResolveInterfaceMethodToDefaultImplementationOnType(query, out target);
                                result += target is null ? 1 : RuntimeHelpers.GetHashCode(target);
                            }
                        }
                        break;
                    case 4:
                    case 5:
                        if (sample.Units[(int)Unit.FieldsExamined] != 0)
                            foreach (FieldDesc field in type.GetFields())
                                result += field.IsLiteral ? 2 : 1;
                        if (sample.Units[(int)Unit.MethodsExamined] != 0)
                        {
                            foreach (MethodDesc method in type.GetMethods())
                            {
                                result += method.HasInstantiation ? 2 : 1;
                                if (operation == 5 && !method.HasInstantiation)
                                    result += RuntimeHelpers.GetHashCode(method.GetCanonMethodTarget(CanonicalFormKind.Specific));
                            }
                        }
                        break;
                    case 6:
                        foreach (MethodDesc method in sample.QuerySlots)
                            result += RuntimeHelpers.GetHashCode(sample.Factory.VirtualMethodUse(method));
                        break;
                    case 7:
                        foreach (MethodDesc method in sample.CanonQuerySlots)
                            result += RuntimeHelpers.GetHashCode(sample.Factory.DelegateTargetVirtualMethod(method));
                        break;
                    case 8:
                        foreach (FieldDesc field in sample.ReflectedFields)
                            result += RuntimeHelpers.GetHashCode(sample.Factory.ReflectedField(field));
                        foreach (MethodDesc method in sample.ReflectedMethods)
                            result += RuntimeHelpers.GetHashCode(sample.Factory.ReflectedMethod(method));
                        break;
                    case 9:
                        sample.Factory.MetadataManager.GetConditionalDependenciesDueToEETypePresence(
                            sample.Sink, sample.Factory, type, allocated: true);
                        using (var entries = sample.Sink.Drain())
                            while (entries.MoveNext())
                                result++;
                        break;
                    case 10:
                        foreach (OverridePair pair in sample.Notes)
                        {
                            sample.Factory.MetadataManager.NoteOverridingMethod(pair.Declaration, pair.Implementation, pair.Origin);
                            result++;
                        }
                        break;
                    case 11:
                        foreach (OverridePair pair in sample.Overrides)
                            sample.Factory.MetadataManager.GetDependenciesForOverridingMethod(
                                sample.Sink, sample.Factory, pair.Declaration, pair.Implementation);
                        using (var entries = sample.Sink.Drain())
                            while (entries.MoveNext())
                                result++;
                        break;
                    case 12:
                        foreach (FieldDesc field in sample.MemberPolicyFields)
                            result += sample.Factory.MetadataManager.IsReflectionBlocked(field) ? 2 : 1;
                        foreach (MethodDesc method in sample.MemberPolicyMethods)
                            result += sample.Factory.MetadataManager.IsReflectionBlocked(method) ? 2 : 1;
                        break;
                    case 13:
                        foreach (MethodDesc method in sample.ConstraintMethods)
                        {
                            Instantiation instantiation = TypeExtensions.GetInstantiationThatMeetsConstraints(method.Instantiation, allowCanon: true);
                            result += instantiation.IsNull ? -1 : instantiation.Length;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }
            }
        }
        finally
        {
            s_replaying = false;
        }

        return result;
    }

    public static string ExportJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            int caseIndex = 0;
            foreach (Stage stage in s_stages.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("Stage", stage.Name);
                writer.WriteNumber("NonSelfAnnotationValidations", stage.NonSelfValidations);
                writer.WriteNumber("UniqueAnnotationValidationPairs", stage.ValidationPairs.Count);
                writer.WritePropertyName("Total");
                WriteAggregate(writer, stage.Total);
                writer.WriteStartArray("Families");
                foreach (var family in stage.Families)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Name", family.Key.ToString());
                    writer.WritePropertyName("Work");
                    WriteAggregate(writer, family.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("Buckets");
                foreach (Bucket bucket in stage.Buckets.Values)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("CaseIndex", caseIndex++);
                    writer.WriteString("Name", bucket.Name);
                    writer.WriteNumber("SampleCount", bucket.Cases.Count);
                    writer.WritePropertyName("Work");
                    WriteAggregate(writer, bucket.Total);
                    writer.WriteStartArray("Samples");
                    foreach (Case sample in bucket.Cases)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("Type", sample.Node.Type.ToString());
                        writer.WriteNumber("OriginalCpuNs", sample.OriginalCpu);
                        writer.WriteNumber("OriginalAllocated", sample.OriginalAllocated);
                        writer.WriteNumber("Edges", sample.Expected.Length);
                        writer.WriteNumber("SlotQueries", sample.QuerySlots?.Length ?? 0);
                        writer.WriteNumber("ReflectionQueries", (sample.ReflectedMethods?.Length ?? 0) + (sample.ReflectedFields?.Length ?? 0));
                        writer.WriteNumber("MemberCanonicalizations", sample.MemberCanonicalizations);
                        writer.WriteNumber("NoteCalls", sample.Notes.Length);
                        writer.WriteNumber("OverrideCalls", sample.Overrides.Length);
                        writer.WriteNumber("ConstraintCalls", sample.ConstraintMethods?.Length ?? 0);
                        WriteUnits(writer, sample.Units);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteAggregate(Utf8JsonWriter writer, Aggregate aggregate)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Calls", aggregate.Calls);
        writer.WriteNumber("CpuNs", aggregate.Cpu);
        writer.WriteNumber("Allocated", aggregate.Allocated);
        writer.WriteNumber("Edges", aggregate.Edges);
        writer.WriteStartObject("Sections");
        for (int i = 0; i < aggregate.SectionCpu.Length; i++)
        {
            writer.WriteStartObject(((Section)i).ToString());
            writer.WriteNumber("CpuNs", aggregate.SectionCpu[i]);
            writer.WriteNumber("Calls", aggregate.SectionCalls[i]);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        WriteUnits(writer, aggregate.Units);
        writer.WriteEndObject();
    }

    private static void WriteUnits(Utf8JsonWriter writer, long[] units)
    {
        writer.WriteStartObject("Units");
        for (int i = 0; i < units.Length; i++)
            writer.WriteNumber(((Unit)i).ToString(), units[i]);
        writer.WriteEndObject();
    }
}
