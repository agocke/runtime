// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysisFramework;
using Internal.TypeSystem;

namespace ILCompiler.ObjectWriter;

public abstract partial class ObjectWriter
{
    private sealed class PreparedTypeEmission
    {
        private readonly ObjectNode.ObjectData[] _data;
        private readonly ConcurrentDictionary<int, ExceptionDispatchInfo> _errors = new();

        public PreparedTypeEmission(int count) => _data = new ObjectNode.ObjectData[count];

        public void RecordError(int index, Exception error)
            => _errors[index] = ExceptionDispatchInfo.Capture(error);

        public ObjectNode.ObjectData Take(int index)
        {
            if (_errors.TryGetValue(index, out ExceptionDispatchInfo error))
                error.Throw();
            ObjectNode.ObjectData value = _data[index];
            _data[index] = null;

            return value;
        }

        public void Run(List<(int Index, EETypeNode Node)> work, NodeFactory factory, int workers)
        {
            using var phase = CompilationEventSource.StartPhase("ObjectEmission.PrepareTypes", work.Count);
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = workers }, item =>
            {
                if (_errors.ContainsKey(item.Index))
                    return;
                try
                {
                    _data[item.Index] = item.Node.GetHydratedData(factory);
                }
                catch (Exception error)
                {
                    RecordError(item.Index, error);
                }
            });
        }
    }

    private PreparedTypeEmission PrepareTypeEmission(IReadOnlyCollection<DependencyNode> nodes)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("DOTNET_ILC_PORTFOLIO_TYPES"), out int workers)
            || workers <= 0)
            return null;

        List<(int Index, EETypeNode Node)> work = new();
        int index = 0;
        foreach (DependencyNode dependency in nodes)
        {
            int current = index++;
            if (dependency is not EETypeNode node || node.ShouldSkipEmittingObjectNode(_nodeFactory))
                continue;
            if (_nodeFactory.ObjectInterner.GetDeduplicatedSymbol(_nodeFactory, node) != node)
                continue;
            work.Add((current, node));
        }
        PreparedTypeEmission prepared = new(nodes.Count);
        using (CompilationEventSource.StartPhase("ObjectEmission.PrepareTypeLayouts", work.Count))
        {
            foreach (var item in work)
            {
                try
                {
                    if (item.Node is ConstructedEETypeNode && !item.Node.Type.IsArrayTypeWithoutGenericInterfaces())
                    {
                        _nodeFactory.SealedVTable(item.Node.Type.ConvertToCanonForm(CanonicalFormKind.Specific))
                            .BuildSealedVTableSlots(_nodeFactory, relocsOnly: false);
                    }
                }
                catch (Exception error)
                {
                    prepared.RecordError(item.Index, error);
                }
            }
        }
        prepared.Run(work, _nodeFactory, workers);

        return prepared;
    }
}
