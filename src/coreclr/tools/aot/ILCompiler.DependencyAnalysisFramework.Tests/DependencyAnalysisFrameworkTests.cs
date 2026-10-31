// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;

using ILCompiler.DependencyAnalysisFramework;

using Xunit;

namespace ILCompiler.DependencyAnalysisFramework.Tests
{
    public class DependencyTests
    {
        public DependencyTests()
        {
        }

        /// <summary>
        /// Test on every graph type. Used to ensure that the behavior of the various markers is consistent
        /// </summary>
        /// <param name="testGraph"></param>
        private void TestOnGraphTypes(Action<TestGraph, DependencyAnalyzerBase<TestGraph>> testGraph)
        {
            // Test using the full logging strategy
            TestGraph testGraphFull = new TestGraph();
            DependencyAnalyzerBase<TestGraph> analyzerFull = new DependencyAnalyzer<FullGraphLogStrategy<TestGraph>, TestGraph>(testGraphFull, null);
            testGraphFull.AttachToDependencyAnalyzer(analyzerFull);
            testGraph(testGraphFull, analyzerFull);

            TestGraph testGraphFirstMark = new TestGraph();
            DependencyAnalyzerBase<TestGraph> analyzerFirstMark = new DependencyAnalyzer<FirstMarkLogStrategy<TestGraph>, TestGraph>(testGraphFirstMark, null);
            testGraphFirstMark.AttachToDependencyAnalyzer(analyzerFirstMark);
            testGraph(testGraphFirstMark, analyzerFirstMark);

            TestGraph testGraphNoLog = new TestGraph();
            DependencyAnalyzerBase<TestGraph> analyzerNoLog = new DependencyAnalyzer<NoLogStrategy<TestGraph>, TestGraph>(testGraphNoLog, null);
            testGraphNoLog.AttachToDependencyAnalyzer(analyzerNoLog);
            testGraph(testGraphNoLog, analyzerNoLog);
        }

        [Fact]
        public void TestADependsOnB()
        {
            TestOnGraphTypes((TestGraph testGraph, DependencyAnalyzerBase<TestGraph> analyzer) =>
            {
                testGraph.AddStaticRule("A", "B", "A depends on B");
                testGraph.AddRoot("A", "A is root");
                List<string> results = testGraph.AnalysisResults;

                Assert.Contains("A", results);
                Assert.Contains("B", results);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DiagnosticSnapshotPreservesSinkAndSelectsSuffix(bool includePrefix)
        {
            var graph = new TestGraph();
            var sink = new DependencyNodeCore<TestGraph>.DependencySink();
            var first = graph.GetNode("A");
            var second = graph.GetNode("B");
            var condition = graph.GetNode("C");
            if (includePrefix)
            {
                sink.Add(condition, "Prefix");
                sink.Add(new DependencyNodeCore<TestGraph>.CombinedDependencyListEntry(first, second, "Conditional prefix"));
            }
            var start = sink.GetDiagnosticCounts();
            sink.Add(new DependencyNodeCore<TestGraph>.CombinedDependencyListEntry(second, condition, "Conditional suffix"));
            sink.Add(first, "Unconditional suffix");

            var snapshot = sink.GetDiagnosticSnapshot(start.Unconditional, start.Conditional);
            Assert.Collection(snapshot,
                entry =>
                {
                    Assert.Same(first, entry.Node);
                    Assert.Null(entry.OtherReasonNode);
                    Assert.Equal("Unconditional suffix", entry.Reason);
                },
                entry =>
                {
                    Assert.Same(second, entry.Node);
                    Assert.Same(condition, entry.OtherReasonNode);
                    Assert.Equal("Conditional suffix", entry.Reason);
                });
            Assert.Equal((start.Unconditional + 1, start.Conditional + 1), sink.GetDiagnosticCounts());
            Assert.Empty(sink.GetDiagnosticSnapshot(start.Unconditional + 1, start.Conditional + 1));

            int drained = 0;
            using (var entries = sink.Drain())
            {
                while (entries.MoveNext())
                    drained++;
            }
            Assert.Equal(includePrefix ? 4 : 2, drained);
            Assert.Equal((0, 0), sink.GetDiagnosticCounts());
        }

        [Fact]
        public void TestADependsOnBIfC_NoC()
        {
            TestOnGraphTypes((TestGraph testGraph, DependencyAnalyzerBase<TestGraph> analyzer) =>
            {
                testGraph.AddConditionalRule("A", "C", "B", "A depends on B if C");
                testGraph.AddRoot("A", "A is root");
                List<string> results = testGraph.AnalysisResults;

                Assert.Contains("A", results);
                Assert.DoesNotContain("B", results);
                Assert.DoesNotContain("C", results);
                Assert.True(results.Count == 1);
            });
        }

        [Fact]
        public void TestADependsOnBIfC_HasC()
        {
            TestOnGraphTypes((TestGraph testGraph, DependencyAnalyzerBase<TestGraph> analyzer) =>
            {
                testGraph.AddConditionalRule("A", "C", "B", "A depends on B if C");
                testGraph.AddRoot("A", "A is root");
                testGraph.AddRoot("C", "C is root");
                List<string> results = testGraph.AnalysisResults;

                Assert.Contains("A", results);
                Assert.Contains("B", results);
                Assert.Contains("C", results);
                Assert.True(results.Count == 3);
            });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ConditionalDuplicatesPreserveReachabilityAndReasons(bool markTarget, bool markCondition)
        {
            TestOnGraphTypes((TestGraph graph, DependencyAnalyzerBase<TestGraph> analyzer) =>
            {
                graph.AddConditionalRule("A", "C", "B", "First reason");
                graph.AddConditionalRule("A", "C", "B", "Second reason");
                graph.AddConditionalRule("D", "C", "B", "Other source");
                if (markTarget)
                    graph.AddRoot("B", "Target root");
                graph.AddRoot("A", "Source root");
                graph.AddRoot("D", "Other source root");
                if (markCondition)
                {
                    graph.AddStaticRule("A", "E", "Deferred condition source");
                    graph.AddStaticRule("E", "C", "Condition dependency");
                }

                int markedTargets = 0;
                analyzer.NewMarkedNode += node =>
                {
                    if (ReferenceEquals(node, graph.GetNode("B")))
                        markedTargets++;
                };
                List<string> results = graph.AnalysisResults;
                Assert.Equal(markTarget || markCondition, results.Contains("B"));
                Assert.Equal(markCondition, results.Contains("C"));
                Assert.Equal(!markTarget && markCondition ? 1 : 0, markedTargets);
                Assert.Equal(2 + (markTarget || markCondition ? 1 : 0) + (markCondition ? 2 : 0), results.Count);

                if (markCondition && analyzer is DependencyAnalyzer<FullGraphLogStrategy<TestGraph>, TestGraph>)
                {
                    using var output = new MemoryStream();
                    DgmlWriter.WriteDependencyGraphToStream(output, analyzer, graph);
                    output.Position = 0;
                    using var reader = new StreamReader(output);
                    string dgml = reader.ReadToEnd();
                    Assert.Contains("First reason", dgml);
                    Assert.Contains("Second reason", dgml);
                    Assert.Contains("Other source", dgml);
                }
            });
        }

        [Fact]
        public void TestSimpleDynamicRule()
        {
            TestOnGraphTypes((TestGraph testGraph, DependencyAnalyzerBase<TestGraph> analyzer) =>
            {
                testGraph.SetDynamicDependencyRule((string nodeA, string nodeB) =>
                {
                    if (nodeA.EndsWith("*") && nodeB.StartsWith("*"))
                    {
                        return new Tuple<string, string>(nodeA + nodeB, "DynamicRule");
                    }
                    return null;
                });

                testGraph.AddRoot("A*", "A* is root");
                testGraph.AddRoot("B*", "B* is root");
                testGraph.AddRoot("*C", "*C is root");
                testGraph.AddRoot("*D", "*D is root");
                testGraph.AddRoot("A*B", "A*B is root");
                List<string> results = testGraph.AnalysisResults;

                Assert.Contains("A*", results);
                Assert.Contains("B*", results);
                Assert.Contains("*C", results);
                Assert.Contains("*D", results);
                Assert.Contains("A*B", results);
                Assert.Contains("A**C", results);
                Assert.Contains("A**D", results);
                Assert.Contains("B**C", results);
                Assert.Contains("B**D", results);
                Assert.True(results.Count == 9);
            });
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(2049)]
        public void ConditionalFanoutPreservesCallbackOrderAcrossWaves(int fanout)
        {
            TestGraph context = new();
            var graph = new DependencyAnalyzer<NoLogStrategy<TestGraph>, TestGraph>(context, null);
            context.AttachToDependencyAnalyzer(graph);
            List<string> order = new();
            List<string> expected = new();
            graph.NewMarkedNode += node => order.Add(((TestGraph.TestNode)node).Data);
            for (int wave = 0; wave < 4; wave++)
            {
                string source = $"Source{wave}";
                string condition = $"Condition{wave}";
                context.AddStaticRule(source, $"Delay{wave}", "Delay activation");
                context.AddStaticRule($"Delay{wave}", condition, "Activate condition");
                context.AddConditionalRule(source, "Never", "Unreachable", "Inactive condition");
                context.AddConditionalRule(source, "Never", "Source0", "Already marked target");
                expected.Add(source);
                expected.Add($"Delay{wave}");
                expected.Add(condition);
                for (int target = 0; target < fanout; target++)
                {
                    string name = $"Target{wave}.{target}";
                    context.AddConditionalRule(source, condition, name, "First reason");
                    context.AddConditionalRule(source, condition, name, "Duplicate target");
                    expected.Add(name);
                }
                if (wave < 3)
                    context.AddStaticRule($"Target{wave}.0", $"Source{wave + 1}", "Next wave");
            }
            context.AddRoot("Source0", "Root");
            List<string> result = context.AnalysisResults;
            Assert.DoesNotContain("Unreachable", result);
            Assert.Equal(expected.Count, result.Count);
            Assert.Equal(expected, order);
            graph.ComputeMarkedNodes();
            Assert.Equal(expected, order);
        }

        private void BuildGraphUsingAllTypesOfRules(TestGraph testGraph, DependencyAnalyzerBase<TestGraph> analyzer)
        {
            testGraph.SetDynamicDependencyRule((string nodeA, string nodeB) =>
            {
                if (nodeA.EndsWith("*") && nodeB.StartsWith("*"))
                {
                    return new Tuple<string, string>(nodeA + nodeB, "DynamicRule");
                }
                return null;
            });

            testGraph.AddConditionalRule("A**C", "B**D", "D", "A**C depends on D if B**D");
            testGraph.AddStaticRule("D", "E", "D depends on E");

            // Rules to ensure that there are some nodes that have multiple reasons to exist
            testGraph.AddStaticRule("A*", "E", "A* depends on E");
            testGraph.AddStaticRule("*C", "E", "*C depends on E");

            testGraph.AddRoot("A*", "A* is root");
            testGraph.AddRoot("B*", "B* is root");
            testGraph.AddRoot("*C", "*C is root");
            testGraph.AddRoot("*D", "*D is root");
            testGraph.AddRoot("A*B", "A*B is root");

            List<string> results = testGraph.AnalysisResults;
            Assert.Contains("A*", results);
            Assert.Contains("B*", results);
            Assert.Contains("*C", results);
            Assert.Contains("*D", results);
            Assert.Contains("A*B", results);
            Assert.Contains("A**C", results);
            Assert.Contains("A**D", results);
            Assert.Contains("B**C", results);
            Assert.Contains("B**D", results);
            Assert.Contains("D", results);
            Assert.Contains("E", results);
            Assert.True(results.Count == 11);
        }

        [Fact]
        public void TestDGMLOutput()
        {
            Dictionary<string, string> dgmlOutputs = new Dictionary<string, string>();
            TestOnGraphTypes((TestGraph testGraph, DependencyAnalyzerBase<TestGraph> analyzer) =>
            {
                BuildGraphUsingAllTypesOfRules(testGraph, analyzer);
                MemoryStream dgmlOutput = new MemoryStream();
                DgmlWriter.WriteDependencyGraphToStream(dgmlOutput, analyzer, testGraph);
                dgmlOutput.Seek(0, SeekOrigin.Begin);
                TextReader tr = new StreamReader(dgmlOutput);
                dgmlOutputs[analyzer.GetType().FullName] = tr.ReadToEnd();
            });

            foreach (var pair in dgmlOutputs)
            {
                int nodeCount = pair.Value.Split(new string[] { "<Node " }, StringSplitOptions.None).Length - 1;
                int edgeCount = pair.Value.Split(new string[] { "<Link " }, StringSplitOptions.None).Length - 1;
                if (pair.Key.Contains("FullGraph"))
                {
                    Assert.Equal(21, nodeCount);
                    Assert.Equal(23, edgeCount);
                }
                else if (pair.Key.Contains("FirstMark"))
                {
                    // There are 2 edges in the all types of rules graph that are duplicates. Note that the edge count is 
                    // 2 less than the full graph edge count
                    Assert.Equal(21, nodeCount);
                    Assert.Equal(21, edgeCount);
                }
                else
                {
                    Assert.Contains("NoLog", pair.Key);
                    Assert.Equal(11, nodeCount);
                    Assert.Equal(0, edgeCount);
                }
            }
        }
    }

    public class CompilationEventTests
    {
        [Fact]
        public void GraphStatisticsPreservesTypedPayload()
        {
            using var listener = new PhaseListener();
            CompilationEventSource.Log.GraphStatistics(7, 1L << 33, 1L << 34);
            Assert.Contains((7, 1L << 33, 1L << 34), listener.GraphEvents);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NestedPhasesReportCompletion(bool throwException)
        {
            using var listener = new PhaseListener();
            try
            {
                using var outer = CompilationEventSource.StartPhase("Test.Outer", 7);
                using var inner = CompilationEventSource.StartPhase("Test.Inner", 3);
                if (throwException)
                    throw new InvalidOperationException("Test exception");
            }
            catch (InvalidOperationException) when (throwException)
            {
            }

            Assert.Equal(new[]
            {
                (1, "Test.Outer", 7L),
                (1, "Test.Inner", 3L),
                (2, "Test.Inner", 0L),
                (2, "Test.Outer", 0L)
            }, listener.Events);
        }

        private sealed class PhaseListener : EventListener
        {
            public List<(int, string, long)> Events { get; } = new();
            public ConcurrentQueue<(int, long, long)> GraphEvents { get; } = new();

            protected override void OnEventSourceCreated(EventSource source)
            {
                if (source.Name == "Microsoft-ILCompiler-Compilation")
                    EnableEvents(source, EventLevel.Informational);
            }

            protected override void OnEventWritten(EventWrittenEventArgs args)
            {
                if (args.EventId == 4)
                    GraphEvents.Enqueue(((int)args.Payload[0], (long)args.Payload[1], (long)args.Payload[2]));
                if (args.Payload?.Count > 0 && args.Payload[0] is string name && name.StartsWith("Test.", StringComparison.Ordinal))
                    Events.Add((args.EventId, name, args.EventId == 1 ? (long)args.Payload[1] : 0));
            }
        }
    }
}
