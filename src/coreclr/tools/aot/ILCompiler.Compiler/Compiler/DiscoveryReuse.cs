// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Threading;
using ILCompiler.Dataflow;
using Internal.TypeSystem;

namespace ILCompiler;

public static class DiscoveryReuse
{
    public static bool Annotations { get; } =
        Environment.GetEnvironmentVariable("DOTNET_ILC_DISCOVERY_REUSE") is "annotations" or "both";
    public static bool Bindings { get; } =
        Environment.GetEnvironmentVariable("DOTNET_ILC_DISCOVERY_REUSE") is "bindings" or "both";
    public static bool Profile { get; } =
        Environment.GetEnvironmentVariable("DOTNET_ILC_DISCOVERY_REUSE_PROFILE") == "1";

    internal static long AnnotationRequests;
    internal static long AnnotationBuilds;
    internal static long AnnotationKeys;
    internal static long BindingRequests;
    internal static long BindingBuilds;
    internal static long BindingKeys;
    internal static long BindingBypasses;
    internal static long NegativeBindings;

    static DiscoveryReuse()
    {
        if (Profile)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.WriteLine(FormattableString.Invariant(
                $"ILC_DISCOVERY_REUSE\t{{\"AnnotationRequests\":{AnnotationRequests},\"AnnotationBuilds\":{AnnotationBuilds},\"AnnotationKeys\":{AnnotationKeys},\"BindingRequests\":{BindingRequests},\"BindingBuilds\":{BindingBuilds},\"BindingKeys\":{BindingKeys},\"BindingBypasses\":{BindingBypasses},\"NegativeBindings\":{NegativeBindings}}}"));
        }
    }
}

public readonly struct RequirementsFacts
{
    public readonly byte Requires;
    public readonly byte Scope;

    public RequirementsFacts(byte requires, byte scope)
    {
        Requires = requires;
        Scope = scope;
    }

    public bool Mismatches(RequirementsFacts other, int attributeIndex)
        => (((Requires & ~other.Scope) | (other.Requires & ~Scope)) & (1 << attributeIndex)) != 0;
}

public sealed class AnnotationFactsCache
{
    private static readonly string[] s_attributes =
    [
        DiagnosticUtilities.RequiresUnreferencedCodeAttribute,
        DiagnosticUtilities.RequiresDynamicCodeAttribute,
        DiagnosticUtilities.RequiresAssemblyFilesAttribute,
    ];

    private readonly ConcurrentDictionary<MethodDesc, RequirementsFacts> _facts = new();

    public RequirementsFacts Get(MethodDesc method)
    {
        if (DiscoveryReuse.Profile)
            Interlocked.Increment(ref DiscoveryReuse.AnnotationRequests);
        if (_facts.TryGetValue(method, out RequirementsFacts facts))
            return facts;

        facts = Compute(method);
        if (DiscoveryReuse.Profile)
            Interlocked.Increment(ref DiscoveryReuse.AnnotationBuilds);
        if (_facts.TryAdd(method, facts))
        {
            if (DiscoveryReuse.Profile)
                Interlocked.Increment(ref DiscoveryReuse.AnnotationKeys);
            return facts;
        }

        return _facts[method];
    }

    public static RequirementsFacts Compute(MethodDesc method)
    {
        byte requires = 0;
        byte scope = 0;
        for (int i = 0; i < s_attributes.Length; i++)
        {
            if (method.DoesMethodRequire(s_attributes[i], out _))
                requires |= (byte)(1 << i);
            if (method.IsInRequiresScope(s_attributes[i]))
                scope |= (byte)(1 << i);
        }

        return new RequirementsFacts(requires, scope);
    }
}

public enum InterfaceBindingKind
{
    Instance,
    Static,
    Default,
}

public readonly struct InterfaceBindingResult
{
    public readonly MethodDesc Implementation;
    public readonly DefaultInterfaceMethodResolution Resolution;

    public InterfaceBindingResult(MethodDesc implementation, DefaultInterfaceMethodResolution resolution = default)
    {
        Implementation = implementation;
        Resolution = resolution;
    }
}

public sealed class InterfaceBindingCache
{
    private readonly ConcurrentDictionary<(TypeDesc Type, MethodDesc Method, InterfaceBindingKind Kind), InterfaceBindingResult> _bindings = new();
    private readonly Func<TypeDesc, MethodDesc, InterfaceBindingKind, InterfaceBindingResult> _resolve;

    public InterfaceBindingCache(Func<TypeDesc, MethodDesc, InterfaceBindingKind, InterfaceBindingResult> resolve)
        => _resolve = resolve;

    public InterfaceBindingResult Get(TypeDesc type, MethodDesc method, InterfaceBindingKind kind)
    {
        if (!DiscoveryReuse.Bindings || !type.IsTypeDefinition)
        {
            if (DiscoveryReuse.Profile)
                Interlocked.Increment(ref DiscoveryReuse.BindingBypasses);
            return _resolve(type, method, kind);
        }

        if (DiscoveryReuse.Profile)
            Interlocked.Increment(ref DiscoveryReuse.BindingRequests);
        var key = (type, method, kind);
        if (_bindings.TryGetValue(key, out InterfaceBindingResult result))
            return result;

        result = _resolve(type, method, kind);
        if (DiscoveryReuse.Profile)
            Interlocked.Increment(ref DiscoveryReuse.BindingBuilds);
        if (_bindings.TryAdd(key, result))
        {
            if (DiscoveryReuse.Profile)
            {
                Interlocked.Increment(ref DiscoveryReuse.BindingKeys);
                if (result.Implementation is null)
                    Interlocked.Increment(ref DiscoveryReuse.NegativeBindings);
            }
            return result;
        }

        return _bindings[key];
    }
}
