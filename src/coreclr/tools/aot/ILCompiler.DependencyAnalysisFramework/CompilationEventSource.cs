// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;

namespace ILCompiler.DependencyAnalysisFramework
{
    [EventSource(Name = "Microsoft-ILCompiler-Compilation")]
    public sealed class CompilationEventSource : EventSource
    {
        public static CompilationEventSource Log { get; } = new CompilationEventSource();

        [NonEvent]
        public static long ReadThreadCpuNanoseconds() => GraphWorkProfile.ThreadCpuNanoseconds();

        [NonEvent]
        public static WorkScope StartWork(string name, object node)
        {
            return new WorkScope(GraphWorkProfile.Current?.Measure(name, node));
        }

        public readonly struct WorkScope : IDisposable
        {
            private readonly GraphWorkProfile.WorkScope? _work;

            internal WorkScope(GraphWorkProfile.WorkScope? work)
            {
                _work = work;
            }

            public void Dispose()
            {
                _work?.Dispose();
            }
        }

        [NonEvent]
        public static PhaseScope StartPhase(string name, long workItems = 0)
        {
            if (!Log.IsEnabled())
                return default;

            Log.PhaseStart(name, workItems);
            return new PhaseScope(name);
        }

        public readonly struct PhaseScope : IDisposable
        {
            private readonly string _name;

            internal PhaseScope(string name)
            {
                _name = name;
            }

            public void Dispose()
            {
                if (_name is not null)
                    Log.PhaseStop(_name);
            }
        }

        [Event(1, Level = EventLevel.Informational)]
        public void PhaseStart(string name, long workItems)
        {
            if (IsEnabled())
                WriteEvent(1, name, workItems);
        }

        [Event(2, Level = EventLevel.Informational)]
        public void PhaseStop(string name)
        {
            if (IsEnabled())
                WriteEvent(2, name);
        }

        [Event(3, Level = EventLevel.Informational)]
        public void TableStatistics(string name, int count, int distinctHashes, int maximumMultiplicity, long collidingPairs)
        {
            if (IsEnabled())
                WriteEvent(3, name, count, distinctHashes, maximumMultiplicity, collidingPairs);
        }

        [Event(4, Level = EventLevel.Informational)]
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The event payload contains only primitive types.")]
        public void GraphStatistics(int nodes, long edges, long dynamicCandidates)
        {
            if (IsEnabled())
                WriteEvent(4, new object[] { nodes, edges, dynamicCandidates });
        }

        [Event(5, Level = EventLevel.Informational)]
        public void ObjectStatistics(int nodes, long dataBytes, long relocations, int symbols)
        {
            if (IsEnabled())
                WriteEvent(5, nodes, dataBytes, relocations, symbols);
        }
    }
}
