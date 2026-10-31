// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ILCompiler.DependencyAnalysisFramework;

internal sealed class GraphWorkProfile
{
    private const int InitialSamples = 32;
    private readonly Dictionary<(Counter Parent, string Region, string Operation, Type Type), Counter> _counters = new();
    private readonly Dictionary<string, (long Cpu, long Wall)> _regions = new();
    private readonly Random _random;
    private readonly HashSet<string> _exactOperations;
    private readonly int _samplePeriod;
    private readonly long _clockOverhead;
    private string _region = "Outside";
    private Counter _active;
    [ThreadStatic]
    private static GraphWorkProfile s_current;

    internal static GraphWorkProfile Current => s_current;

    internal static bool Enabled { get; } =
        AppContext.TryGetSwitch("ILCompiler.ProfileGraphWork", out bool enabled) && enabled;

    internal GraphWorkProfile()
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Graph thread-CPU profiling requires 64-bit Linux.");

        int seed = int.TryParse(Environment.GetEnvironmentVariable("DOTNET_ILC_WORK_SEED"), out int value) ? value : 2027;
        _random = new Random(seed);
        _samplePeriod = int.TryParse(Environment.GetEnvironmentVariable("DOTNET_ILC_WORK_PERIOD"), out value) && value > 0 ? value : 64;
        string exactOperations = Environment.GetEnvironmentVariable("DOTNET_ILC_WORK_EXACT");
        if (!string.IsNullOrEmpty(exactOperations))
            _exactOperations = new HashSet<string>(exactOperations.Split(','), StringComparer.Ordinal);

        const int CalibrationIterations = 100_000;
        for (int i = 0; i < 1_000; i++)
            ThreadCpuNanoseconds();
        long start = ThreadCpuNanoseconds();
        for (int i = 0; i < CalibrationIterations; i++)
            ThreadCpuNanoseconds();
        _clockOverhead = (ThreadCpuNanoseconds() - start) / CalibrationIterations;
    }

    internal WorkScope Measure(string operation, object node)
    {
        var key = (_active, _region, operation, node?.GetType());
        if (!_counters.TryGetValue(key, out Counter counter))
        {
            counter = new Counter(_counters.Count + 1, _active);
            _counters.Add(key, counter);
        }

        counter.Calls++;
        int weight = counter.Calls <= InitialSamples || _exactOperations?.Contains(operation) == true
            ? 1 : _random.Next(_samplePeriod) == 0 ? _samplePeriod : 0;

        // Select children independently: forcing them when a parent is sampled would
        // inflate that parent's estimate with disproportionately many clock reads.
        var scope = new WorkScope(this, counter, weight);
        _active = counter;
        return scope;
    }

    internal RegionScope MeasureRegion(string region) => new RegionScope(this, region);

    internal void WriteStatistics(int nodes)
    {
        Console.Error.WriteLine(FormattableString.Invariant(
            $"ILC_WORK_CLOCK\t{nodes}\t{_clockOverhead}\t{_samplePeriod}\t{Stopwatch.Frequency}"));
        foreach (var entry in _regions)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"ILC_WORK_TOTAL\t{nodes}\t{entry.Key}\t{entry.Value.Cpu}\t{entry.Value.Wall}"));
        }

        foreach (var entry in _counters)
        {
            Counter counter = entry.Value;
            double inclusiveError = Math.Sqrt(counter.InclusiveSquares * _samplePeriod * (_samplePeriod - 1));
            Console.Error.WriteLine(FormattableString.Invariant(
                $"ILC_WORK\t{nodes}\t{counter.Id}\t{counter.Parent?.Id ?? 0}\t{entry.Key.Region}\t{entry.Key.Operation}\t{entry.Key.Type?.FullName ?? "-"}\t{counter.Calls}\t{counter.Samples}\t{counter.InclusiveCpu}\t{counter.InclusiveCpu - counter.ChildCpu}\t{inclusiveError:R}\t{counter.MaximumCpu}"));
        }
    }

    internal static long ThreadCpuNanoseconds()
    {
        const int ClockThreadCpuTimeId = 3;
        if (ClockGetTime(ClockThreadCpuTimeId, out Timespec time) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        return time.Seconds * 1_000_000_000 + time.Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clock, out Timespec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    internal sealed class Counter
    {
        public readonly int Id;
        public readonly Counter Parent;
        public long Calls;
        public long Samples;
        public long InclusiveCpu;
        public long ChildCpu;
        public double InclusiveSquares;
        public long MaximumCpu;

        public Counter(int id, Counter parent)
        {
            Id = id;
            Parent = parent;
        }
    }

    internal readonly struct WorkScope : IDisposable
    {
        private readonly GraphWorkProfile _profile;
        private readonly Counter _counter;
        private readonly int _weight;
        private readonly long _startCpu;

        internal WorkScope(GraphWorkProfile profile, Counter counter, int weight)
        {
            _profile = profile;
            _counter = counter;
            _weight = weight;
            _startCpu = weight == 0 ? 0 : ThreadCpuNanoseconds();
        }

        public void Dispose()
        {
            Debug.Assert(ReferenceEquals(_profile._active, _counter));
            _profile._active = _counter.Parent;

            if (_weight == 0)
                return;

            long cpu = ThreadCpuNanoseconds() - _startCpu - _profile._clockOverhead;
            _counter.Samples++;
            _counter.InclusiveCpu += cpu * _weight;
            if (_counter.Parent is not null)
                _counter.Parent.ChildCpu += cpu * _weight;
            _counter.MaximumCpu = Math.Max(_counter.MaximumCpu, cpu);
            if (_weight > 1)
                _counter.InclusiveSquares += (double)cpu * cpu;
        }
    }

    internal sealed class RegionScope : IDisposable
    {
        private readonly GraphWorkProfile _profile;
        private readonly string _region;
        private readonly string _previous;
        private readonly long _startCpu;
        private readonly long _startWall;
        private readonly GraphWorkProfile _previousProfile;

        internal RegionScope(GraphWorkProfile profile, string region)
        {
            Debug.Assert(profile._active is null);
            _profile = profile;
            _region = region;
            _previous = profile._region;
            _previousProfile = s_current;
            s_current = profile;
            profile._region = region;
            _startWall = Stopwatch.GetTimestamp();
            _startCpu = ThreadCpuNanoseconds();
        }

        public void Dispose()
        {
            long cpu = ThreadCpuNanoseconds() - _startCpu;
            long wall = Stopwatch.GetTimestamp() - _startWall;
            _profile._regions.TryGetValue(_region, out var previous);
            _profile._regions[_region] = (previous.Cpu + cpu, previous.Wall + wall);
            _profile._region = _previous;
            s_current = _previousProfile;
        }
    }
}
