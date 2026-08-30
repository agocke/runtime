// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysisFramework;

namespace ILCompiler
{
    /// <summary>
    /// Represents the level of dependency tracking within the dependency analysis system.
    /// </summary>
    public enum DependencyTrackingLevel
    {
        /// <summary>
        /// Tracking disabled. This is the most performant and memory efficient option.
        /// </summary>
        None,

        /// <summary>
        /// The graph keeps track of the first dependency.
        /// </summary>
        First,

        /// <summary>
        /// The graph keeps track of all dependencies.
        /// </summary>
        All
    }

    internal static class DependencyTrackingLevelExtensions
    {
        public static DependencyAnalyzerBase<NodeFactory> CreateDependencyGraph(this DependencyTrackingLevel trackingLevel, NodeFactory factory, IComparer<DependencyNodeCore<NodeFactory>> comparer = null, int parallelism = 1)
        {
            // Choose which dependency graph implementation to use based on the amount of logging requested.
            switch (trackingLevel)
            {
                case DependencyTrackingLevel.None:
                    if (EventSourceLogStrategy<NodeFactory>.IsEventSourceEnabled)
                        return new DependencyAnalyzer<EventSourceLogStrategy<NodeFactory>, NodeFactory>(factory, comparer, parallelism);
                    else
                        return new DependencyAnalyzer<NoLogStrategy<NodeFactory>, NodeFactory>(factory, comparer, parallelism);

                case DependencyTrackingLevel.First:
                    return new DependencyAnalyzer<FirstMarkLogStrategy<NodeFactory>, NodeFactory>(factory, comparer, parallelism);

                case DependencyTrackingLevel.All:
                    return new DependencyAnalyzer<FullGraphLogStrategy<NodeFactory>, NodeFactory>(factory, comparer, parallelism);

                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
