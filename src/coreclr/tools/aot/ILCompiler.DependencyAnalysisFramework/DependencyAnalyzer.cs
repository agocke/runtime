// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

namespace ILCompiler.DependencyAnalysisFramework
{
    /// <summary>
    /// Implement a dependency analysis framework. This works much like a Garbage Collector's mark algorithm
    /// in that it finds a set of nodes from an initial root set.
    ///
    /// However, in contrast to a typical GC in addition to simple edges from a node, there may also
    /// be conditional edges where a node has a dependency if some other specific node exists in the
    /// graph, and dynamic edges in which a node has a dependency if some other node exists in the graph,
    /// but what that other node might be is not known until it may exist in the graph.
    ///
    /// This analyzer also attempts to maintain a serialized state of why nodes are in the graph
    /// with strings describing the reason a given node was added to the graph. The degree of logging
    /// is configurable via the MarkStrategy
    ///
    /// </summary>
    public sealed class DependencyAnalyzer<MarkStrategy, DependencyContextType> : DependencyAnalyzerBase<DependencyContextType> where MarkStrategy : struct, IDependencyAnalysisMarkStrategy<DependencyContextType>
    {
#pragma warning disable SA1129 // Do not use default value type constructor
        private MarkStrategy _marker = new MarkStrategy();
#pragma warning restore SA1129 // Do not use default value type constructor
        private DependencyContextType _dependencyContext;
        private IComparer<DependencyNodeCore<DependencyContextType>> _resultSorter;

        private RandomInsertStack<DependencyNodeCore<DependencyContextType>> _markStack;
        private List<DependencyNodeCore<DependencyContextType>> _markedNodes = new List<DependencyNodeCore<DependencyContextType>>();
        private ImmutableArray<DependencyNodeCore<DependencyContextType>> _markedNodesFinal;
        private List<DependencyNodeCore<DependencyContextType>> _rootNodes = new List<DependencyNodeCore<DependencyContextType>>();
        private Dictionary<int, List<DependencyNodeCore<DependencyContextType>>> _deferredStaticDependencies = new Dictionary<int, List<DependencyNodeCore<DependencyContextType>>>();
        private List<DependencyNodeCore<DependencyContextType>> _dynamicDependencyInterestingList = new List<DependencyNodeCore<DependencyContextType>>();
        private List<DynamicDependencyNode> _markedNodesWithDynamicDependencies = new List<DynamicDependencyNode>();
        private bool _newDynamicDependenciesMayHaveAppeared;
        private readonly DependencyNodeCore<DependencyContextType>.DependencySink _dependencySink = new DependencyNodeCore<DependencyContextType>.DependencySink();

        private Dictionary<DependencyNodeCore<DependencyContextType>, HashSet<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry>> _conditional_dependency_store = new Dictionary<DependencyNodeCore<DependencyContextType>, HashSet<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry>>();
        private bool _markingCompleted;
        private readonly bool _collectStatistics = CompilationEventSource.Log.IsEnabled();
        private long _edgesCommitted;
        private long _dynamicCandidates;
        private readonly ConditionalAudit _conditionalAudit =
            AppContext.TryGetSwitch("ILCompiler.ProfileLookups", out bool audit) && audit ? new ConditionalAudit() : null;
        private readonly GraphWorkProfile _workProfile = GraphWorkProfile.Enabled ? new GraphWorkProfile() : null;

        private sealed class ConditionalAudit
        {
            public long PendingEntries;
            public long PendingAlreadyMarked;
            public long NewSets;
            public long DuplicateEntries;
            public long Queries;
            public long EmptyQueries;
            public long SatisfiedSets;
            public long ActivatedEntries;
            public long ActivatedAlreadyMarked;
            public int MaximumSet;
        }

        private sealed class RandomInsertStack<T>
        {
            private List<T> _nodes = new List<T>();
            private readonly Random _randomizer;

            public RandomInsertStack(Random randomizer = null)
            {
                _randomizer = randomizer;
            }

            public T Pop()
            {
                T node = _nodes[_nodes.Count - 1];
                _nodes.RemoveAt(_nodes.Count - 1);
                return node;
            }

            public int Count => _nodes.Count;

            public void Push(T node)
            {
                if (_randomizer == null)
                {
                    _nodes.Add(node);
                }
                else
                {
                    int index = _randomizer.Next(_nodes.Count);
                    _nodes.Insert(index, node);
                }
            }
        }

        private struct DynamicDependencyNode
        {
            private DependencyNodeCore<DependencyContextType> _node;
            private int _next;

            public DynamicDependencyNode(DependencyNodeCore<DependencyContextType> node)
            {
                _node = node;
                _next = 0;
            }

            public void MarkNewDynamicDependencies(DependencyAnalyzer<MarkStrategy, DependencyContextType> analyzer)
            {
                DependencyNodeCore<DependencyContextType>.DependencySink sink = analyzer._dependencySink;
                if (analyzer._collectStatistics)
                    analyzer._dynamicCandidates += analyzer._dynamicDependencyInterestingList.Count - _next;
                using (analyzer._workProfile?.Measure("DynamicDiscovery", _node))
                {
                    _node.SearchDynamicDependencies(analyzer._dynamicDependencyInterestingList, _next, sink, analyzer._dependencyContext);
                }
                analyzer.CommitDependencies(sink, _node);
                _next = analyzer._dynamicDependencyInterestingList.Count;
            }
        }

        // Api surface
        public DependencyAnalyzer(DependencyContextType dependencyContext, IComparer<DependencyNodeCore<DependencyContextType>> resultSorter)
        {
            _dependencyContext = dependencyContext;
            _resultSorter = resultSorter;
            _marker.AttachContext(dependencyContext);

            Random stackPopRandomizer = null;
            if (int.TryParse(Environment.GetEnvironmentVariable("CoreRT_DeterminismSeed"), out int seed))
            {
                // Expose output file determinism bugs in our system by randomizing the order nodes are pushed
                // onto the mark stack.
                stackPopRandomizer = new Random(seed);
            }
            _markStack = new RandomInsertStack<DependencyNodeCore<DependencyContextType>>(stackPopRandomizer);
        }

        /// <summary>
        /// Add a root node
        /// </summary>
        public sealed override void AddRoot(DependencyNodeCore<DependencyContextType> rootNode, string reason)
        {
            if (AddToMarkStack(rootNode, reason, null, null))
            {
                _rootNodes.Add(rootNode);
            }
        }

        public sealed override ImmutableArray<DependencyNodeCore<DependencyContextType>> MarkedNodeList
        {
            get
            {
                if (!_markingCompleted)
                {
                    throw new InvalidOperationException();
                }

                return _markedNodesFinal;
            }
        }

        public sealed override event Action<DependencyNodeCore<DependencyContextType>> NewMarkedNode;

        public sealed override event Action<List<DependencyNodeCore<DependencyContextType>>> ComputeDependencyRoutine;

        public sealed override event Action<int> ComputingDependencyPhaseChange;

        private IEnumerable<DependencyNodeCore<DependencyContextType>> MarkedNodesEnumerable()
        {
            if (_markedNodesFinal != null)
                return _markedNodesFinal;
            else
                return _markedNodes;
        }

        public sealed override void VisitLogNodes(IDependencyAnalyzerLogNodeVisitor<DependencyContextType> logNodeVisitor)
        {
            foreach (DependencyNodeCore<DependencyContextType> node in MarkedNodesEnumerable())
            {
                logNodeVisitor.VisitNode(node);
            }
            _marker.VisitLogNodes(MarkedNodesEnumerable(), logNodeVisitor);
        }

        public sealed override void VisitLogEdges(IDependencyAnalyzerLogEdgeVisitor<DependencyContextType> logEdgeVisitor)
        {
            _marker.VisitLogEdges(MarkedNodesEnumerable(), logEdgeVisitor);
        }


        /// <summary>
        /// Called by the algorithm to ensure that this set of nodes is processed such that static dependencies are computed.
        /// </summary>
        /// <param name="deferredStaticDependencies">List of nodes which must have static dependencies computed</param>
        private void ComputeDependencies(List<DependencyNodeCore<DependencyContextType>> deferredStaticDependencies)
        {
            ComputeDependencyRoutine?.Invoke(deferredStaticDependencies);
        }

        // Internal details
        private void AddStaticDependencies(DependencyNodeCore<DependencyContextType> node)
        {
            DependencyNodeCore<DependencyContextType>.DependencySink sink = _dependencySink;
            using (_workProfile?.Measure("StaticDiscovery", node))
            {
                node.AddStaticDependencies(sink, _dependencyContext);
            }

            if (node.HasConditionalStaticDependencies)
            {
                using (_workProfile?.Measure("ConditionalDiscovery", node))
                {
                    node.AddConditionalDependencies(sink, _dependencyContext);
                }
            }

            CommitDependencies(sink, node);
        }

        private void CommitDependencies(
            DependencyNodeCore<DependencyContextType>.DependencySink sink,
            DependencyNodeCore<DependencyContextType> source)
        {
            using GraphWorkProfile.WorkScope? workScope = _workProfile?.Measure("Commit", source);
            using DependencyNodeCore<DependencyContextType>.DependencySink.DrainEnumerator dependencies = sink.Drain();
            while (dependencies.MoveNext())
            {
                if (_collectStatistics)
                    _edgesCommitted++;
                var otherReasonNode = dependencies.OtherReasonNode;
                if (otherReasonNode is null || otherReasonNode.Marked)
                {
                    AddToMarkStack(dependencies.Dependency, dependencies.Reason, source, otherReasonNode);
                    continue;
                }

                if (_conditionalAudit is not null)
                {
                    _conditionalAudit.PendingEntries++;
                    if (dependencies.Dependency.Marked)
                        _conditionalAudit.PendingAlreadyMarked++;
                }
                if (!_conditional_dependency_store.TryGetValue(otherReasonNode, out HashSet<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry> storedDependencySet))
                {
                    storedDependencySet = new HashSet<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry>();
                    _conditional_dependency_store.Add(otherReasonNode, storedDependencySet);
                    if (_conditionalAudit is not null)
                        _conditionalAudit.NewSets++;
                }

                // Swap out other reason node as we're storing that as the dictionary key
                var conditionalDependencyStoreEntry =
                    new DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry(dependencies.Dependency, source, dependencies.Reason);
                bool added = storedDependencySet.Add(conditionalDependencyStoreEntry);
                if (_conditionalAudit is not null)
                {
                    if (!added)
                        _conditionalAudit.DuplicateEntries++;
                    _conditionalAudit.MaximumSet = Math.Max(_conditionalAudit.MaximumSet, storedDependencySet.Count);
                }
            }
        }

        private int _currentDependencyPhase;

        private void GetStaticDependencies(DependencyNodeCore<DependencyContextType> node)
        {
            if (node.StaticDependenciesAreComputed)
            {
                AddStaticDependencies(node);
            }
            else
            {
                int dependencyPhase = Math.Max(node.DependencyPhaseForDeferredStaticComputation, _currentDependencyPhase);
                if (!_deferredStaticDependencies.TryGetValue(dependencyPhase, out var deferredPerPhaseDependencies))
                {
                    deferredPerPhaseDependencies = new List<DependencyNodeCore<DependencyContextType>>();
                    _deferredStaticDependencies.Add(dependencyPhase, deferredPerPhaseDependencies);
                }
                deferredPerPhaseDependencies.Add(node);
            }
        }

        private void ProcessMarkStack()
        {
            do
            {
                while (_markStack.Count > 0)
                {
                    // Pop the top node of the mark stack
                    DependencyNodeCore<DependencyContextType> currentNode = _markStack.Pop();
                    using GraphWorkProfile.WorkScope? workScope = _workProfile?.Measure("ProcessNode", currentNode);

                    Debug.Assert(currentNode.Marked);

                    // Only some marked objects are interesting for dynamic dependencies
                    // store those in a separate list to avoid excess scanning over non-interesting
                    // nodes during dynamic dependency discovery
                    if (currentNode.InterestingForDynamicDependencyAnalysis)
                    {
                        _dynamicDependencyInterestingList.Add(currentNode);
                        _newDynamicDependenciesMayHaveAppeared = true;
                    }

                    // Add all static dependencies to the mark stack
                    GetStaticDependencies(currentNode);

                    RegisterDynamicDependencies(currentNode);

                    // If this new node satisfies any stored conditional dependencies,
                    // add them to the mark stack
                    HashSet<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry> storedDependencySet;
                    if (_conditionalAudit is not null)
                    {
                        _conditionalAudit.Queries++;
                        if (_conditional_dependency_store.Count == 0)
                            _conditionalAudit.EmptyQueries++;
                    }
                    if (_conditional_dependency_store.TryGetValue(currentNode, out storedDependencySet))
                    {
                        if (_conditionalAudit is not null)
                            _conditionalAudit.SatisfiedSets++;
                        foreach (DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry newlySatisfiedDependency in storedDependencySet)
                        {
                            if (_conditionalAudit is not null)
                            {
                                _conditionalAudit.ActivatedEntries++;
                                if (newlySatisfiedDependency.Node.Marked)
                                    _conditionalAudit.ActivatedAlreadyMarked++;
                            }
                            AddToMarkStack(newlySatisfiedDependency.Node, newlySatisfiedDependency.Reason, newlySatisfiedDependency.OtherReasonNode, currentNode);
                        }

                        _conditional_dependency_store.Remove(currentNode);
                    }
                }

                // Find new dependencies introduced by dynamic dependencies
                if (_newDynamicDependenciesMayHaveAppeared)
                {
                    _newDynamicDependenciesMayHaveAppeared = false;
                    for (int i = 0; i < _markedNodesWithDynamicDependencies.Count; i++)
                    {
                        DynamicDependencyNode dynamicNode = _markedNodesWithDynamicDependencies[i];
                        dynamicNode.MarkNewDynamicDependencies(this);

                        // Update the copy in the list
                        _markedNodesWithDynamicDependencies[i] = dynamicNode;
                    }
                }
            } while (_markStack.Count != 0);
        }

        public override void ComputeMarkedNodes()
        {
            using (PerfEventSource.StartStopEvents.GraphProcessingEvents())
            {
                if (_markingCompleted)
                    return;

                do
                {
                    // Run mark stack algorithm as much as possible
                    using (PerfEventSource.StartStopEvents.DependencyAnalysisEvents())
                    using (CompilationEventSource.StartPhase("Graph.Mark"))
                    using (_workProfile?.MeasureRegion("Mark"))
                    {
                        ProcessMarkStack();
                    }

                    // Compute all dependencies which were not ready during the ProcessMarkStack step
                    _deferredStaticDependencies.TryGetValue(_currentDependencyPhase, out var deferredDependenciesInCurrentPhase);

                    if (deferredDependenciesInCurrentPhase != null)
                    {
                        using (_workProfile?.MeasureRegion("DeferredCompute"))
                        {
                            ComputeDependencies(deferredDependenciesInCurrentPhase);
                        }
                        using (_workProfile?.MeasureRegion("DeferredCommit"))
                        {
                            foreach (DependencyNodeCore<DependencyContextType> node in deferredDependenciesInCurrentPhase)
                            {
                                Debug.Assert(node.StaticDependenciesAreComputed);
                                AddStaticDependencies(node);
                            }
                        }

                        deferredDependenciesInCurrentPhase.Clear();
                    }

                    if (_markStack.Count == 0)
                    {
                        // Time to move to next deferred dependency phase.

                        // 1. Remove old deferred dependency list(if it exists)
                        if (deferredDependenciesInCurrentPhase != null)
                        {
                            _deferredStaticDependencies.Remove(_currentDependencyPhase);
                        }

                        // 2. Increment current dependency phase
                        _currentDependencyPhase++;

                        // 3. Notify that new dependency phase has been entered
                        ComputingDependencyPhaseChange?.Invoke(_currentDependencyPhase);
                    }
                } while ((_markStack.Count != 0) || (_deferredStaticDependencies.Count != 0));

                using (CompilationEventSource.StartPhase("Graph.SortAndFreeze", _markedNodes.Count))
                {
                    if (_resultSorter != null)
                        _markedNodes.MergeSortAllowDuplicates(_resultSorter);

                    _markedNodesFinal = _markedNodes.ToImmutableArray();
                }
                _markedNodes = null;
                _markingCompleted = true;
                _workProfile?.WriteStatistics(_markedNodesFinal.Length);
                if (_collectStatistics)
                    CompilationEventSource.Log.GraphStatistics(_markedNodesFinal.Length, _edgesCommitted, _dynamicCandidates);
                if (_conditionalAudit is not null)
                {
                    long remaining = 0;
                    long remainingMarked = 0;
                    long repeatedTargets = 0;
                    foreach (var set in _conditional_dependency_store.Values)
                    {
                        var targets = new HashSet<DependencyNodeCore<DependencyContextType>>();
                        foreach (var entry in set)
                        {
                            remaining++;
                            if (entry.Node.Marked)
                                remainingMarked++;
                            if (!targets.Add(entry.Node))
                                repeatedTargets++;
                        }
                    }
                    ConditionalAudit audit = _conditionalAudit;
                    Console.Error.WriteLine(string.Join("\t", "ILC_CONDITIONAL", _markedNodesFinal.Length,
                        audit.PendingEntries, audit.PendingAlreadyMarked, audit.NewSets, audit.DuplicateEntries,
                        audit.Queries, audit.EmptyQueries, audit.SatisfiedSets, audit.ActivatedEntries,
                        audit.ActivatedAlreadyMarked, audit.MaximumSet, _conditional_dependency_store.Count,
                        remaining, remainingMarked, repeatedTargets));
                }
            }
        }

        private void RegisterDynamicDependencies(DependencyNodeCore<DependencyContextType> node)
        {
            if (node.HasDynamicDependencies)
            {
                _newDynamicDependenciesMayHaveAppeared = true;
                _markedNodesWithDynamicDependencies.Add(new DynamicDependencyNode(node));
            }
        }

        private bool AddToMarkStack(DependencyNodeCore<DependencyContextType> node, string reason, DependencyNodeCore<DependencyContextType> reason1, DependencyNodeCore<DependencyContextType> reason2)
        {
            bool marked;
            using (_workProfile?.Measure("MarkStrategy", null))
            {
                marked = _marker.MarkNode(node, reason1, reason2, reason);
            }
            if (marked)
            {
                if (PerfEventSource.Log.IsEnabled())
                    PerfEventSource.Log.AddedNodeToMarkStack();

                _markStack.Push(node);
                _markedNodes.Add(node);

                using (_workProfile?.Measure("MarkCallbacks", node))
                {
                    node.CallOnMarked(_dependencyContext);
                    NewMarkedNode?.Invoke(node);
                }

                return true;
            }

            return false;
        }
    }
}
