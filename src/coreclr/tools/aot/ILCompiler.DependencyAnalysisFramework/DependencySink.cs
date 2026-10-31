// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ILCompiler.DependencyAnalysisFramework
{
    public abstract partial class DependencyNodeCore<DependencyContextType>
    {
        public sealed class DependencySink :
            IDependencySink,
            IConditionalDependencySink
        {
            private readonly List<DependencyNodeCore<DependencyContextType>.DependencyListEntry> _dependencies =
                new List<DependencyNodeCore<DependencyContextType>.DependencyListEntry>();
            private readonly List<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry> _combinedDependencies =
                new List<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry>();

            /// <summary>
            /// A single-use enumerator that clears its sink when disposed.
            /// </summary>
            public struct DrainEnumerator : IDisposable
            {
                private readonly DependencySink _sink;
                private List<DependencyNodeCore<DependencyContextType>.DependencyListEntry>.Enumerator _enumerator;
                private List<DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry>.Enumerator _combinedEnumerator;
                private bool _enumeratingCombinedDependencies;

                internal DrainEnumerator(DependencySink sink)
                {
                    _sink = sink;
                    _enumerator = sink._dependencies.GetEnumerator();
                    _combinedEnumerator = sink._combinedDependencies.GetEnumerator();
                    _enumeratingCombinedDependencies = false;
                }

                public DependencyNodeCore<DependencyContextType> Dependency =>
                    _enumeratingCombinedDependencies ? _combinedEnumerator.Current.Node : _enumerator.Current.Node;

                public string Reason =>
                    _enumeratingCombinedDependencies ? _combinedEnumerator.Current.Reason : _enumerator.Current.Reason;

                public DependencyNodeCore<DependencyContextType>? OtherReasonNode =>
                    _enumeratingCombinedDependencies ? _combinedEnumerator.Current.OtherReasonNode : null;

                /// <summary>
                /// Advances to the next dependency.
                /// </summary>
                public bool MoveNext()
                {
                    if (!_enumeratingCombinedDependencies)
                    {
                        if (_enumerator.MoveNext())
                        {
                            return true;
                        }

                        _enumeratingCombinedDependencies = true;
                    }

                    return _combinedEnumerator.MoveNext();
                }

                /// <inheritdoc/>
                public void Dispose()
                {
                    _enumerator.Dispose();
                    _combinedEnumerator.Dispose();
                    _sink?.Clear();
                }
            }

            /// <summary>
            /// Returns a single-use enumerator over the current dependencies that clears the sink when disposed.
            /// </summary>
            public DrainEnumerator Drain()
            {
                return new DrainEnumerator(this);
            }

            public (int Unconditional, int Conditional) GetDiagnosticCounts()
                => (_dependencies.Count, _combinedDependencies.Count);

            public readonly struct DiagnosticEntry
            {
                public readonly DependencyNodeCore<DependencyContextType> Node;
                public readonly DependencyNodeCore<DependencyContextType>? OtherReasonNode;
                public readonly string Reason;

                internal DiagnosticEntry(DependencyNodeCore<DependencyContextType> node,
                    DependencyNodeCore<DependencyContextType>? otherReasonNode, string reason)
                {
                    Node = node;
                    OtherReasonNode = otherReasonNode;
                    Reason = reason;
                }
            }

            public DiagnosticEntry[] GetDiagnosticSnapshot(int unconditionalStart, int conditionalStart)
            {
                var result = new DiagnosticEntry[
                    _dependencies.Count - unconditionalStart + _combinedDependencies.Count - conditionalStart];
                int index = 0;
                for (int i = unconditionalStart; i < _dependencies.Count; i++)
                {
                    DependencyListEntry entry = _dependencies[i];
                    result[index++] = new DiagnosticEntry(entry.Node, null, entry.Reason);
                }
                for (int i = conditionalStart; i < _combinedDependencies.Count; i++)
                {
                    CombinedDependencyListEntry entry = _combinedDependencies[i];
                    result[index++] = new DiagnosticEntry(entry.Node, entry.OtherReasonNode, entry.Reason);
                }

                return result;
            }

            private void Clear()
            {
                _dependencies.Clear();
                _combinedDependencies.Clear();
            }

            public void Add(DependencyNodeCore<DependencyContextType> node, string reason)
            {
                Add(new DependencyNodeCore<DependencyContextType>.DependencyListEntry(node, reason));
            }

            public void AddConditional(
                DependencyNodeCore<DependencyContextType> node,
                DependencyNodeCore<DependencyContextType> otherReasonNode,
                string reason)
            {
                Debug.Assert(otherReasonNode is not null);
                Add(new DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry(node, otherReasonNode, reason));
            }

            public void Add(object node, string reason)
            {
                Add((DependencyNodeCore<DependencyContextType>)node, reason);
            }

            public void AddConditional(
                object node,
                object otherReasonNode,
                string reason)
            {
                Debug.Assert(otherReasonNode is not null);
                Add(new DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry(node, otherReasonNode, reason));
            }

            public void Add(DependencyNodeCore<DependencyContextType>.DependencyListEntry dependency)
            {
                _dependencies.Add(dependency);
            }

            public void Add(DependencyNodeCore<DependencyContextType>.CombinedDependencyListEntry dependency)
            {
                _combinedDependencies.Add(dependency);
            }

            public void AddRange(params ReadOnlySpan<DependencyNodeCore<DependencyContextType>.DependencyListEntry> dependencies)
            {
                foreach (DependencyNodeCore<DependencyContextType>.DependencyListEntry dependency in dependencies)
                {
                    Add(dependency);
                }
            }
        }
    }
}
