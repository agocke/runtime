// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ILCompiler.DependencyAnalysis;
using Internal.TypeSystem;

namespace ILCompiler;

public static class DiscoveryAlgorithms
{
    public static bool Pairs { get; } = Enabled("pairs");
    public static bool Interfaces { get; } = Enabled("interfaces");
    public static bool Reflection { get; } = Enabled("reflection");
    public static bool Dictionary { get; } = Enabled("dictionary");
    public static bool Validate { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_ALGORITHM_VALIDATE") == "1";
    public static bool Count { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_ALGORITHM_COUNTS") == "1";

    private static readonly long[] s_counts = new long[Enum.GetValues<Work>().Length];

    public enum Work
    {
        ClassPairs,
        ValidatedClassPairs,
        DefinitionInterfaceMethods,
        ConcreteInterfaceMethods,
        ReflectionPlanBuilds,
        ReflectionPlanUses,
        ReflectionPlanMethods,
        ReflectionPlanGenericMethods,
        DictionaryGroups,
        DictionaryRegistrations,
        DictionaryEligibleMethods,
        DictionaryMarkedBodies,
        DictionaryActivePairs,
    }

    private static bool Enabled(string name)
    {
        string mode = Environment.GetEnvironmentVariable("DOTNET_ILC_DISCOVERY_ALGORITHM");
        return mode == "all" || mode == name || (mode == "core" && name != "dictionary");
    }

    public static void Note(Work work)
    {
        if (Count)
            Interlocked.Increment(ref s_counts[(int)work]);
    }

    public static void Dump(TextWriter writer)
    {
        foreach (Work work in Enum.GetValues<Work>())
            writer.WriteLine($"ALGORITHM_WORK\t{work}\t{Volatile.Read(ref s_counts[(int)work])}");
    }

    public static IEnumerable<(MethodDesc Slot, MethodDesc Implementation)> ClassSlots(TypeDesc type)
    {
        if (Pairs)
        {
            var algorithm = (CachingVirtualMethodAlgorithm)type.Context.GetVirtualMethodAlgorithmForType(type);
            foreach ((MethodDesc slot, MethodDesc implementation) in algorithm.EnumerateResolvedSlots(type))
            {
                Note(Work.ClassPairs);
                if (Validate && !slot.HasInstantiation)
                {
                    if (type.FindVirtualFunctionTargetMethodOnObjectType(slot) != implementation)
                        throw new InvalidOperationException("Resolved pair differs from the original resolver: " + type + " / " + slot);
                    Note(Work.ValidatedClassPairs);
                }
                yield return (slot, implementation);
            }
        }
        else
        {
            foreach (MethodDesc slot in type.EnumAllVirtualSlots())
                yield return (slot, slot.HasInstantiation ? null : type.FindVirtualFunctionTargetMethodOnObjectType(slot));
        }
    }

    public static MethodDesc BindInterfaceMethod(MethodDesc definitionMethod, DefType concreteInterface)
    {
        Note(Work.ConcreteInterfaceMethods);
        return definitionMethod.Context.GetMethodForInstantiatedType(
            definitionMethod.GetTypicalMethodDefinition(), (InstantiatedType)concreteInterface);
    }
}
