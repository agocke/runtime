// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Generic;
using ILCompiler.DependencyAnalysis;
using Internal.TypeSystem;

namespace ILCompiler;

internal sealed class MethodReflectabilityPlan
{
    internal readonly struct Entry
    {
        internal readonly MethodDesc GenericDefinition;
        internal readonly ReflectedMethodNode Target;
        internal readonly ReflectedMethodNode Condition;

        internal Entry(MethodDesc genericDefinition, ReflectedMethodNode target, ReflectedMethodNode condition)
        {
            GenericDefinition = genericDefinition;
            Target = target;
            Condition = condition;
        }
    }

    private readonly ConcurrentDictionary<TypeDesc, Entry[]> _plans = new();

    internal Entry[] Get(TypeDesc concreteType, MetadataManager metadata, NodeFactory factory)
    {
        DiscoveryAlgorithms.Note(DiscoveryAlgorithms.Work.ReflectionPlanUses);
        TypeDesc canonicalType = concreteType.ConvertToCanonForm(CanonicalFormKind.Specific);
        if (_plans.TryGetValue(canonicalType, out Entry[] entries))
            return entries;

        var result = new List<Entry>();
        foreach (MethodDesc method in canonicalType.GetMethods())
        {
            if (method.HasInstantiation)
            {
                result.Add(new Entry(method.GetTypicalMethodDefinition(), null, null));
            }
            else if (!metadata.IsReflectionBlocked(method))
            {
                result.Add(new Entry(null,
                    factory.ReflectedMethod(method.GetCanonMethodTarget(CanonicalFormKind.Specific)),
                    factory.ReflectedMethod(method.GetTypicalMethodDefinition())));
            }
        }
        entries = result.ToArray();
        DiscoveryAlgorithms.Note(DiscoveryAlgorithms.Work.ReflectionPlanBuilds);
        return _plans.GetOrAdd(canonicalType, entries);
    }
}
