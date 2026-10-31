// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Internal.TypeSystem;

namespace ILCompiler;

public static class NodeHashAudit
{
    private static readonly List<Action<TextWriter, string>> s_writers = new();
    private static int s_nextId;
    private static readonly List<Func<IBenchmarkCase>> s_benchmarks = new();
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_HASH_AUDIT") == "1";
    private static bool Identity { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_NODE_HASH") == "identity";
    private static bool CompilerIdentity { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_COMPILER_HASH") == "identity";
    private static bool Capture { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_HASH_CAPTURE") == "1";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LookupHash(TypeSystemEntity key)
        => CompilerIdentity ? RuntimeHelpers.GetHashCode(key) : key.GetHashCode();

    public static (ConcurrentDictionary<TKey, TValue> Cache, Func<TKey, TValue> Creator) CreateCache<TKey, TValue>(
        Func<TKey, TValue> creator, IEqualityComparer<TKey> comparer = null, bool allowIdentity = true)
    {
        bool eligible = allowIdentity && Comparer<TKey>.SupportsIdentity;
        bool identity = eligible && Identity;
        if (!Enabled && !identity && !Capture)
            return (new ConcurrentDictionary<TKey, TValue>(comparer), creator);

        Comparer<TKey> audit = new(comparer ?? EqualityComparer<TKey>.Default, identity, eligible);
        ConcurrentDictionary<TKey, TValue> cache = new(audit);
        if (Capture && eligible && typeof(TValue).Name is
            "ReflectedMethodNode" or "GenericLookupResult" or "NativeLayoutTypeHandleGenericDictionarySlotNode")
        {
            lock (s_benchmarks)
                s_benchmarks.Add(() => new BenchmarkCase<TKey, TValue>(cache, comparer ?? EqualityComparer<TKey>.Default));
        }
        if (Enabled)
        {
            WeakReference<ConcurrentDictionary<TKey, TValue>> weak = new(cache);
            lock (s_writers)
            {
                s_writers.Add((writer, phase) =>
                {
                    weak.TryGetTarget(out ConcurrentDictionary<TKey, TValue> live);
                    audit.Write(writer, phase, live);
                });
            }
            Func<TKey, TValue> original = creator;
            creator = key =>
            {
                Interlocked.Increment(ref audit.Creates);
                return original(key);
            };
        }

        return (cache, creator);
    }

    public static void WriteStatistics(TextWriter writer, string phase)
    {
        Action<TextWriter, string>[] writers;
        lock (s_writers)
            writers = s_writers.ToArray();
        foreach (Action<TextWriter, string> write in writers)
            write(writer, phase);
        writer.Flush();
    }

    public static object[] GetBenchmarkCases()
    {
        Dictionary<string, IBenchmarkCase> selected = new();
        foreach (Func<IBenchmarkCase> create in s_benchmarks)
        {
            IBenchmarkCase candidate = create();
            if (candidate.Count < 100)
                continue;
            if (!selected.TryGetValue(candidate.Name, out IBenchmarkCase existing) || candidate.Count > existing.Count)
                selected[candidate.Name] = candidate;
        }
        object[] cases = new object[selected.Count];
        int index = 0;
        foreach (IBenchmarkCase benchmark in selected.Values)
        {
            benchmark.Validate();
            cases[index++] = benchmark;
        }
        return cases;
    }

    public static string DescribeBenchmark(object benchmark) => ((IBenchmarkCase)benchmark).Description;
    public static long RunBenchmark(object benchmark, int algorithm) => ((IBenchmarkCase)benchmark).Run(algorithm);

    private interface IBenchmarkCase
    {
        string Name { get; }
        string Description { get; }
        int Count { get; }
        void Validate();
        long Run(int algorithm);
    }

    private sealed class BenchmarkCase<TKey, TValue> : IBenchmarkCase
    {
        private readonly ConcurrentDictionary<TKey, TValue> _original;
        private readonly ConcurrentDictionary<TKey, TValue> _identity;
        private readonly TKey[] _keys;

        internal BenchmarkCase(ConcurrentDictionary<TKey, TValue> source, IEqualityComparer<TKey> comparer)
        {
            _original = new ConcurrentDictionary<TKey, TValue>(source, comparer);
            _identity = new ConcurrentDictionary<TKey, TValue>(source, new Comparer<TKey>(comparer, true, true));
            _keys = new TKey[Math.Min(4096, source.Count)];
            int index = 0;
            int step = Math.Max(1, source.Count / Math.Max(1, _keys.Length));
            foreach (KeyValuePair<TKey, TValue> entry in source)
            {
                if (index % step == 0 && index / step < _keys.Length)
                    _keys[index / step] = entry.Key;
                index++;
            }
        }

        public string Name => typeof(TValue).Name;
        public string Description => $"{typeof(TKey)} / {typeof(TValue)}: {Count} retained entries, {_keys.Length} uniform hit keys";
        public int Count => _original.Count;

        public void Validate()
        {
            if (_original.Count != _identity.Count)
                throw new InvalidOperationException("Identity hash changed key cardinality.");
            foreach (KeyValuePair<TKey, TValue> entry in _original)
            {
                if (!_identity.TryGetValue(entry.Key, out TValue value) || !ReferenceEquals(entry.Value, value))
                    throw new InvalidOperationException("Identity hash changed lookup results.");
            }
        }

        public long Run(int algorithm)
        {
            ConcurrentDictionary<TKey, TValue> cache = algorithm == 0 ? _original : _identity;
            long hits = 0;
            foreach (TKey key in _keys)
            {
                if (cache.TryGetValue(key, out _))
                    hits++;
            }
            return hits;
        }
    }

    private sealed class Comparer<TKey>(IEqualityComparer<TKey> inner, bool identity, bool eligible) : IEqualityComparer<TKey>
    {
        private readonly int _id = Interlocked.Increment(ref s_nextId);
        internal static bool SupportsIdentity { get; } =
            typeof(TKey) == typeof(TypeDesc) || typeof(TKey) == typeof(MethodDesc)
            || typeof(TKey) == typeof(FieldDesc) || typeof(TKey) == typeof((MethodDesc, MethodDesc))
            || typeof(TKey) == typeof((MethodDesc, bool)) || typeof(TKey) == typeof((TypeDesc, bool));

        private long _hashes;
        private long _equals;
        private long _unequal;
        internal long Creates;

        public int GetHashCode(TKey key)
        {
            if (Enabled)
                Interlocked.Increment(ref _hashes);
            return identity ? IdentityHash(key) : inner.GetHashCode(key);
        }

        public bool Equals(TKey left, TKey right)
        {
            bool equal = inner.Equals(left, right);
            if (Enabled)
            {
                Interlocked.Increment(ref _equals);
                if (!equal)
                    Interlocked.Increment(ref _unequal);
            }
            return equal;
        }

        private static int IdentityHash(TKey key)
        {
            if (key is ValueTuple<MethodDesc, MethodDesc> methods)
                return HashCode.Combine(RuntimeHelpers.GetHashCode(methods.Item1), RuntimeHelpers.GetHashCode(methods.Item2));
            if (key is ValueTuple<MethodDesc, bool> method)
                return HashCode.Combine(RuntimeHelpers.GetHashCode(method.Item1), method.Item2);
            if (key is ValueTuple<TypeDesc, bool> type)
                return HashCode.Combine(RuntimeHelpers.GetHashCode(type.Item1), type.Item2);

            return RuntimeHelpers.GetHashCode(key);
        }

        internal void Write<TValue>(TextWriter writer, string phase, ConcurrentDictionary<TKey, TValue> cache)
        {
            Dictionary<int, int> originalHashes = new();
            Dictionary<int, int> identityHashes = new();
            long originalPairs = 0;
            long identityPairs = 0;
            int originalMaximum = 0;
            int identityMaximum = 0;
            int entries = 0;
            if (cache is not null)
            {
                foreach (KeyValuePair<TKey, TValue> entry in cache)
                {
                    Add(originalHashes, inner.GetHashCode(entry.Key), ref originalPairs, ref originalMaximum);
                    if (eligible)
                        Add(identityHashes, IdentityHash(entry.Key), ref identityPairs, ref identityMaximum);
                    entries++;
                }
            }
            writer.WriteLine(string.Join("\t", "NODE_HASH", phase, typeof(TKey), typeof(TValue),
                _id, identity, cache is null ? -1 : entries, Volatile.Read(ref _hashes), Volatile.Read(ref _equals),
                Volatile.Read(ref _unequal), Volatile.Read(ref Creates),
                originalHashes.Count, originalMaximum, originalPairs,
                eligible, identityHashes.Count, identityMaximum, identityPairs));
        }

        private static void Add(Dictionary<int, int> hashes, int hash, ref long pairs, ref int maximum)
        {
            hashes.TryGetValue(hash, out int multiplicity);
            hashes[hash] = multiplicity + 1;
            pairs += multiplicity;
            maximum = Math.Max(maximum, multiplicity + 1);
        }
    }
}
