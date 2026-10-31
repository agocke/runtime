// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace ILCompiler;

public interface IMetadataSetStudy
{
    object Snapshot();
    int CapturedCount { get; }
    string Name { get; }
    long Replay(int algorithm);
    bool ValidateReplay();
    object ReplayDiagnostics();
}

public static class MetadataSets
{
    public static bool Profile { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_METADATA_PROFILE") == "1";
    public static bool Capture { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_METADATA_CAPTURE") == "1";
    public static string Mode { get; } = Environment.GetEnvironmentVariable("DOTNET_ILC_METADATA_MODE") ?? "tree";
    private static readonly ConcurrentQueue<IMetadataSetStudy> s_sets = new();
    private static int s_owner;

    public static string NewOwner(string name) => name + "-" + Interlocked.Increment(ref s_owner);
    public static void Register(IMetadataSetStudy set) => s_sets.Enqueue(set);

    public static string Export()
    {
        var result = new List<object>();
        foreach (IMetadataSetStudy set in s_sets)
            result.Add(set.Snapshot());
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    public static object[] GetBenchmarkCases()
    {
        var candidates = new List<IMetadataSetStudy>();
        foreach (IMetadataSetStudy set in s_sets)
        {
            if (set.CapturedCount >= 10000)
                candidates.Add(set);
        }
        candidates.Sort((left, right) => right.CapturedCount.CompareTo(left.CapturedCount));
        if (candidates.Count > 6)
            candidates.RemoveRange(6, candidates.Count - 6);
        return candidates.ToArray();
    }

    public static string Describe(object value) => ((IMetadataSetStudy)value).Name;
    public static long RunBenchmark(object value, int algorithm) => ((IMetadataSetStudy)value).Replay(algorithm);
    public static bool ValidateBenchmark(object value) => ((IMetadataSetStudy)value).ValidateReplay();
    public static object DescribeValidation(object value) => ((IMetadataSetStudy)value).ReplayDiagnostics();
}

public sealed class MetadataSet<T> : IEnumerable<T>, IMetadataSetStudy where T : class
{
    private readonly string _name;
    private readonly IComparer<T> _comparer;
    private readonly SortedSet<T> _tree;
    private readonly HashSet<T> _hash;
    private readonly List<T> _captured;
    private T[] _ordered;
    private long _adds;
    private long _successfulAdds;
    private long _contains;
    private long _enumerations;
    private long _mutationsAfterEnumeration;
    private long _comparisons;
    private long _sortComparisons;
    private long _sorts;
    private long _addTicks;
    private long _containsTicks;
    private long _sortTicks;
    private long _addAllocated;
    private long _sortAllocated;
    private bool _disabled;
    private long _skippedAdds;
    private object _replayDiagnostics;

    public string Owner { get; set; }
    public string Name => Owner + "/" + _name;
    public int CapturedCount => _captured?.Count ?? 0;
    public int Count => _tree is not null ? _tree.Count : _hash.Count;

    public MetadataSet(string name, IComparer<T> comparer, bool? delayed = null)
    {
        _name = name;
        _comparer = comparer;
        bool late = delayed ?? (MetadataSets.Mode == "late"
            || (MetadataSets.Mode == "types" && name != "_methodBodiesGenerated")
            || (MetadataSets.Mode == "bodies" && name == "_methodBodiesGenerated")
            || (MetadataSets.Mode == "prune-hash" && name is "_typesWithEETypesGenerated" or "_typesWithConstructedEETypesGenerated"));
        if (late)
            _hash = new HashSet<T>(ReferenceEqualityComparer.Instance);
        else
            _tree = new SortedSet<T>(MetadataSets.Profile ? new CountingComparer(this, false) : comparer);
        if (MetadataSets.Profile || MetadataSets.Capture)
            _captured = new List<T>();
        if (MetadataSets.Profile || MetadataSets.Capture)
            MetadataSets.Register(this);
    }

    public bool Add(T item)
    {
        if (_disabled)
        {
            if (MetadataSets.Profile)
                Interlocked.Increment(ref _skippedAdds);
            return false;
        }
        _captured?.Add(item);
        if (!MetadataSets.Profile)
            return AddCore(item);

        long start = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool added = AddCore(item);
        Interlocked.Add(ref _addTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref _addAllocated, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Interlocked.Increment(ref _adds);
        if (added)
        {
            Interlocked.Increment(ref _successfulAdds);
            if (Volatile.Read(ref _enumerations) != 0)
                Interlocked.Increment(ref _mutationsAfterEnumeration);
        }
        return added;
    }

    private bool AddCore(T item)
    {
        if (_tree is not null)
            return _tree.Add(item);

        bool added = _hash.Add(item);
        if (added)
            Volatile.Write(ref _ordered, null);
        return added;
    }

    public bool Contains(T item)
    {
        if (_disabled)
            throw new InvalidOperationException(Name + " was queried after scanner collection was disabled.");
        if (!MetadataSets.Profile)
            return _tree is not null ? _tree.Contains(item) : _hash.Contains(item);

        long start = Stopwatch.GetTimestamp();
        bool found = _tree is not null ? _tree.Contains(item) : _hash.Contains(item);
        Interlocked.Add(ref _containsTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _contains);
        return found;
    }

    private T[] GetOrdered()
    {
        T[] ordered = Volatile.Read(ref _ordered);
        if (ordered is not null)
            return ordered;

        long start = MetadataSets.Profile ? Stopwatch.GetTimestamp() : 0;
        long allocated = MetadataSets.Profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
        ordered = new T[_hash.Count];
        _hash.CopyTo(ordered);
        Array.Sort(ordered, MetadataSets.Profile ? new CountingComparer(this, true) : _comparer);
        Volatile.Write(ref _ordered, ordered);
        if (MetadataSets.Profile)
        {
            Interlocked.Increment(ref _sorts);
            Interlocked.Add(ref _sortTicks, Stopwatch.GetTimestamp() - start);
            Interlocked.Add(ref _sortAllocated, GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        return ordered;
    }

    public IEnumerator<T> GetEnumerator()
        => GetOrderedEnumerable().GetEnumerator();

    public IEnumerable<T> GetOrderedEnumerable()
    {
        if (_disabled)
            throw new InvalidOperationException(Name + " was enumerated after scanner collection was disabled.");
        if (MetadataSets.Profile)
            Interlocked.Increment(ref _enumerations);
        return _tree is not null ? _tree : GetOrdered();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public object Snapshot() => new
    {
        Owner,
        Collection = _name,
        Algorithm = _tree is not null ? "tree" : "late",
        Disabled = _disabled,
        SkippedAdds = _skippedAdds,
        Count,
        DistinctIdentities = _captured is not null
            ? new HashSet<T>(_captured, ReferenceEqualityComparer.Instance).Count : (int?)null,
        Adds = _adds,
        SuccessfulAdds = _successfulAdds,
        Contains = _contains,
        Enumerations = _enumerations,
        MutationsAfterEnumeration = _mutationsAfterEnumeration,
        Comparisons = _comparisons,
        SortComparisons = _sortComparisons,
        Sorts = _sorts,
        AddSeconds = (double)_addTicks / Stopwatch.Frequency,
        ContainsSeconds = (double)_containsTicks / Stopwatch.Frequency,
        SortSeconds = (double)_sortTicks / Stopwatch.Frequency,
        AddAllocatedBytes = _addAllocated,
        SortAllocatedBytes = _sortAllocated,
        CapturedCount,
    };

    private T[] ReplayItems(int algorithm)
    {
        if (algorithm == 0)
        {
            var tree = new SortedSet<T>(_comparer);
            foreach (T item in _captured)
                tree.Add(item);
            var ordered = new T[tree.Count];
            tree.CopyTo(ordered);
            return ordered;
        }
        var hash = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (T item in _captured)
            hash.Add(item);
        var result = new T[hash.Count];
        hash.CopyTo(result);
        Array.Sort(result, _comparer);
        return result;
    }

    public long Replay(int algorithm)
    {
        if (_enumerations == 0)
        {
            if (algorithm == 0)
            {
                var tree = new SortedSet<T>(_comparer);
                foreach (T item in _captured)
                    tree.Add(item);
                return tree.Count;
            }
            var hash = new HashSet<T>(ReferenceEqualityComparer.Instance);
            foreach (T item in _captured)
                hash.Add(item);
            return hash.Count;
        }
        T[] ordered = ReplayItems(algorithm);
        long checksum = 0;
        foreach (T item in ordered)
            checksum = unchecked(checksum * 31 + RuntimeHelpers.GetHashCode(item));
        return checksum;
    }

    public bool ValidateReplay()
    {
        T[] tree = ReplayItems(0);
        T[] late = ReplayItems(1);
        if (tree.Length != late.Length)
            return false;
        var differences = new List<object>();
        int mismatches = 0;
        int antisymmetryFailures = 0;
        for (int i = 0; i < tree.Length; i++)
        {
            if (!ReferenceEquals(tree[i], late[i]))
            {
                mismatches++;
                int forward = _comparer.Compare(tree[i], late[i]);
                int reverse = _comparer.Compare(late[i], tree[i]);
                bool invalid = Math.Sign(forward) == Math.Sign(reverse);
                if (invalid)
                    antisymmetryFailures++;
                if (differences.Count < 8 || (invalid && differences.Count < 16))
                    differences.Add(new
                    {
                        Index = i,
                        Tree = DescribeItem(tree[i]),
                        Late = DescribeItem(late[i]),
                        Forward = forward,
                        Reverse = reverse,
                        AntisymmetryFailure = invalid
                    });
            }
        }
        _replayDiagnostics = new
        {
            TreeCount = tree.Length,
            LateCount = late.Length,
            Mismatches = mismatches,
            AntisymmetryFailures = antisymmetryFailures,
            Differences = differences,
            RequiresOrderedEnumeration = _enumerations != 0
        };
        return mismatches == 0;
    }

    public object ReplayDiagnostics() => _replayDiagnostics;

    public void DisableForScanner()
    {
        Debug.Assert(Count == 0);
        _disabled = true;
    }

    private static object DescribeItem(T item)
    {
        object method = item.GetType().GetProperty("Method")?.GetValue(item);
        return new
        {
            NodeType = item.GetType().FullName,
            MethodType = method?.GetType().FullName,
            Display = method?.ToString() ?? item.ToString()
        };
    }

    private sealed class CountingComparer(MetadataSet<T> owner, bool sorting) : IComparer<T>
    {
        public int Compare(T left, T right)
        {
            if (sorting)
                Interlocked.Increment(ref owner._sortComparisons);
            else
                Interlocked.Increment(ref owner._comparisons);
            return owner._comparer.Compare(left, right);
        }
    }
}
