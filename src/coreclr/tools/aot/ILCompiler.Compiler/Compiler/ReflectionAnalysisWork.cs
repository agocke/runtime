// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysisFramework;

namespace ILCompiler;

public static class ReflectionAnalysisWork
{
    private static readonly string s_mode = Environment.GetEnvironmentVariable("DOTNET_ILC_REFLECTION_MODE") ?? "inline";
    private static readonly int s_parallelism = int.Parse(
        Environment.GetEnvironmentVariable("DOTNET_ILC_REFLECTION_PARALLELISM") ?? "32", CultureInfo.InvariantCulture);
    private static long s_ticks;
    private static long s_maximumTicks;
    private static int s_completed;
    private static int s_active;
    private static int s_maximumActive;
    private static int s_runtimeDependencies;
    private static int s_runtimeRoots;

    static ReflectionAnalysisWork()
    {
        if (s_mode is not ("inline" or "serial" or "parallel") || s_parallelism <= 0)
        {
            throw new ArgumentException("Invalid reflection study mode or parallelism.");
        }
    }

    public static bool IsDeferred => s_mode != "inline";

    public static void Run(List<DataflowAnalyzedMethodNode> nodes, NodeFactory factory, int parallelism)
    {
        if (nodes.Count == 0)
        {
            return;
        }
        Debug.Assert(IsDeferred);
        int degree = s_mode == "serial" ? 1 : Math.Min(parallelism, s_parallelism);
        long start = Stopwatch.GetTimestamp();
        using (CompilationEventSource.StartPhase("Reflection.Analysis", nodes.Count))
        {
            if (degree == 1)
            {
                foreach (DataflowAnalyzedMethodNode node in nodes)
                {
                    node.ComputeDependencies(factory);
                }
            }
            else
            {
                Parallel.ForEach(nodes, new ParallelOptions { MaxDegreeOfParallelism = degree },
                    node => node.ComputeDependencies(factory));
            }
        }
        Console.Error.WriteLine(FormattableString.Invariant(
            $"ILC_REFLECTION_BATCH\t{s_mode}\t{nodes.Count}\t{degree}\t{Stopwatch.GetElapsedTime(start).TotalSeconds:F9}"));
    }

    public static AnalysisScope StartAnalysis() => new AnalysisScope();

    public static void RecordRuntimeDependencies(int count)
    {
        Interlocked.Add(ref s_runtimeDependencies, count);
        if (count != 0)
        {
            Interlocked.Increment(ref s_runtimeRoots);
        }
    }

    public static void WriteStatistics(string stage)
    {
        Console.Error.WriteLine(FormattableString.Invariant(
            $"ILC_REFLECTION_TOTAL\t{stage}\t{s_mode}\t{s_completed}\t{s_maximumActive}\t{(double)s_ticks / Stopwatch.Frequency:F9}\t{(double)s_maximumTicks / Stopwatch.Frequency:F9}\t{s_runtimeRoots}\t{s_runtimeDependencies}"));
    }

    public readonly struct AnalysisScope : IDisposable
    {
        private readonly long _start;

        public AnalysisScope()
        {
            _start = Stopwatch.GetTimestamp();
            int active = Interlocked.Increment(ref s_active);
            int maximum = Volatile.Read(ref s_maximumActive);
            while (active > maximum)
            {
                int previous = Interlocked.CompareExchange(ref s_maximumActive, active, maximum);
                if (previous == maximum)
                {
                    break;
                }
                maximum = previous;
            }
        }

        public void Dispose()
        {
            long elapsed = Stopwatch.GetTimestamp() - _start;
            Interlocked.Add(ref s_ticks, elapsed);
            Interlocked.Increment(ref s_completed);
            Interlocked.Decrement(ref s_active);
            long maximum = Volatile.Read(ref s_maximumTicks);
            while (elapsed > maximum)
            {
                long previous = Interlocked.CompareExchange(ref s_maximumTicks, elapsed, maximum);
                if (previous == maximum)
                {
                    break;
                }
                maximum = previous;
            }
        }
    }
}
