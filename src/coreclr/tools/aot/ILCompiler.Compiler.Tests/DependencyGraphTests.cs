// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ILCompiler.Dataflow;
using ILCompiler.DependencyAnalysisFramework;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using Xunit;

using CustomAttributeValue = System.Reflection.Metadata.CustomAttributeValue<Internal.TypeSystem.TypeDesc>;

namespace ILCompiler.Compiler.Tests
{
    //
    // This test uses IL scanner to scan a dependency graph, starting with a
    // single method from the test assembly.
    // It then checks various invariants about the resulting dependency graph.
    // The test method declares these invariants using custom attributes.
    //
    // The invariants to check for are:
    // * Whether an EEType was/was not generated
    // * Whether a method body was/was not generated
    // * Etc.
    //
    // The most valuable tests are the ones that check that something was not
    // generated. These let us create unit tests for size on disk regressions.
    //

    public class DependencyGraphTests
    {
        public static IEnumerable<object[]> GetTestMethods()
        {
            var target = new TargetDetails(TargetArchitecture.X64, TargetOS.Windows, TargetAbi.NativeAot);
            var context = new CompilerTypeSystemContext(target, SharedGenericsMode.CanonicalReferenceTypes, DelegateFeature.All);

            context.InputFilePaths = new Dictionary<string, string> {
                { "Test.CoreLib", @"Test.CoreLib.dll" },
                { "ILCompiler.Compiler.Tests.Assets", @"ILCompiler.Compiler.Tests.Assets.dll" },
                };
            context.ReferenceFilePaths = new Dictionary<string, string>();

            context.SetSystemModule(context.GetModuleForSimpleName("Test.CoreLib"));
            var testModule = context.GetModuleForSimpleName("ILCompiler.Compiler.Tests.Assets");

            bool foundSomethingToCheck = false;
            foreach (var type in testModule.GetType("ILCompiler.Compiler.Tests.Assets"u8, "DependencyGraph"u8).GetNestedTypes())
            {
                foundSomethingToCheck = true;
                yield return new object[] { type.GetMethod("Entrypoint"u8, null) };
            }

            Assert.True(foundSomethingToCheck, "No methods to check?");
        }

        [Theory]
        [MemberData(nameof(GetTestMethods))]
        public void TestDependencyGraphInvariants(EcmaMethod method)
        {
            //
            // Scan the input method
            //

            var context = (CompilerTypeSystemContext)method.Context;
            CompilationModuleGroup compilationGroup = new SingleFileCompilationModuleGroup();

            NativeAotILProvider ilProvider = new NativeAotILProvider();
            CompilerGeneratedState compilerGeneratedState = new CompilerGeneratedState(ilProvider, Logger.Null, disableGeneratedCodeHeuristics: true);

            UsageBasedMetadataManager metadataManager = new UsageBasedMetadataManager(compilationGroup, context,
                new FullyBlockedMetadataBlockingPolicy(), new FullyBlockedManifestResourceBlockingPolicy(),
                null, new NoStackTraceEmissionPolicy(), new NoDynamicInvokeThunkGenerationPolicy(),
                new ILLink.Shared.TrimAnalysis.FlowAnnotations(Logger.Null, ilProvider, compilerGeneratedState), UsageBasedMetadataGenerationOptions.None,
                default, Logger.Null, new Dictionary<string, bool>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

            CompilationBuilder builder = new RyuJitCompilationBuilder(context, compilationGroup)
                .UseILProvider(ilProvider);

            IILScanner scanner = builder.GetILScannerBuilder()
                .UseCompilationRoots(new ICompilationRootProvider[] { new SingleMethodRootProvider(method) })
                .UseMetadataManager(metadataManager)
                .ToILScanner();

            ILScanResults results = scanner.Scan();

            //
            // Check invariants
            //

            const string assetsNamespace = "ILCompiler.Compiler.Tests.Assets";
            bool foundSomethingToCheck = false;

            foreach (var attr in method.GetDecodedCustomAttributes(assetsNamespace, "GeneratesConstructedEETypeAttribute"))
            {
                foundSomethingToCheck = true;
                Assert.Contains((TypeDesc)attr.FixedArguments[0].Value, results.ConstructedEETypes);
            }

            foreach (var attr in method.GetDecodedCustomAttributes(assetsNamespace, "NoConstructedEETypeAttribute"))
            {
                foundSomethingToCheck = true;
                Assert.DoesNotContain((TypeDesc)attr.FixedArguments[0].Value, results.ConstructedEETypes);
            }

            foreach (var attr in method.GetDecodedCustomAttributes(assetsNamespace, "GeneratesMethodBodyAttribute"))
            {
                foundSomethingToCheck = true;
                MethodDesc methodToCheck = GetMethodFromAttribute(attr);
                Assert.Contains(methodToCheck.GetCanonMethodTarget(CanonicalFormKind.Specific), results.CompiledMethodBodies);
            }

            foreach (var attr in method.GetDecodedCustomAttributes(assetsNamespace, "NoMethodBodyAttribute"))
            {
                foundSomethingToCheck = true;
                MethodDesc methodToCheck = GetMethodFromAttribute(attr);
                Assert.DoesNotContain(methodToCheck.GetCanonMethodTarget(CanonicalFormKind.Specific), results.CompiledMethodBodies);
            }

            //
            // Make sure we checked something
            //

            Assert.True(foundSomethingToCheck, "No invariants to check?");
        }

        [Fact]
        public void DependencyAnalyzerParallelWavesMatchSerialAnalysis()
        {
            AnalysisResult serial = RunAnalysis(parallelism: 1);
            AnalysisResult parallel = RunAnalysis(parallelism: 4);

            Assert.Equal(
                new[] { "conditional", "deferred", "deferredTarget", "dynamic", "root", "target1", "target2", "trigger1", "trigger2" },
                serial.MarkedNodes);
            Assert.Equal(serial.MarkedNodes, parallel.MarkedNodes);
            Assert.Equal(new[] { 0, 1 }, serial.DynamicSearchStarts);
            Assert.Equal(serial.DynamicSearchStarts, parallel.DynamicSearchStarts);
            Assert.All(serial.MarkingThreadIds, threadId => Assert.Equal(serial.MarkingThreadIds[0], threadId));
            Assert.All(parallel.MarkingThreadIds, threadId => Assert.Equal(parallel.MarkingThreadIds[0], threadId));
        }

        private static AnalysisResult RunAnalysis(int parallelism)
        {
            var markingThreadIds = new List<int>();
            var dynamicSearchStarts = new List<int>();

            var trigger1 = new TestDependencyNode("trigger1", markingThreadIds, interestingForDynamicDependencyAnalysis: true);
            var trigger2 = new TestDependencyNode("trigger2", markingThreadIds, interestingForDynamicDependencyAnalysis: true);
            var target2 = new TestDependencyNode("target2", markingThreadIds);
            var target1 = new TestDependencyNode("target1", markingThreadIds, staticDependencies: new[] { trigger2 });
            var conditional = new TestDependencyNode("conditional", markingThreadIds);
            var dynamic = new DynamicTestDependencyNode(
                "dynamic",
                markingThreadIds,
                dynamicSearchStarts,
                new Dictionary<TestDependencyNode, TestDependencyNode>
                {
                    [trigger1] = target1,
                    [trigger2] = target2,
                });
            var deferredTarget = new TestDependencyNode("deferredTarget", markingThreadIds);
            var deferred = new DeferredTestDependencyNode("deferred", markingThreadIds, deferredTarget);
            var root = new TestDependencyNode(
                "root",
                markingThreadIds,
                staticDependencies: new TestDependencyNode[] { dynamic, trigger1, deferred },
                conditionalDependencies: new[] { (conditional, trigger2) });

            var analyzer = new DependencyAnalyzer<NoLogStrategy<string>, string>(
                "context",
                Comparer<DependencyNodeCore<string>>.Create((left, right) => string.CompareOrdinal(left.ToString(), right.ToString())),
                parallelism);
            analyzer.ComputeDependencyRoutine += nodes =>
            {
                foreach (DependencyNodeCore<string> node in nodes)
                {
                    if (node is DeferredTestDependencyNode deferredNode)
                    {
                        deferredNode.SetDependenciesComputed();
                    }
                }
            };
            analyzer.AddRoot(root, "root");
            analyzer.ComputeMarkedNodes();

            var markedNodeNames = new string[analyzer.MarkedNodeList.Length];
            for (int i = 0; i < markedNodeNames.Length; i++)
            {
                markedNodeNames[i] = analyzer.MarkedNodeList[i].ToString();
            }

            return new AnalysisResult(markedNodeNames, dynamicSearchStarts.ToArray(), markingThreadIds.ToArray());
        }

        private class TestDependencyNode : DependencyNodeCore<string>
        {
            private readonly string _name;
            private readonly List<int> _markingThreadIds;
            private readonly TestDependencyNode[] _staticDependencies;
            private readonly (TestDependencyNode Node, TestDependencyNode Condition)[] _conditionalDependencies;
            private readonly bool _interestingForDynamicDependencyAnalysis;

            public TestDependencyNode(
                string name,
                List<int> markingThreadIds,
                TestDependencyNode[] staticDependencies = null,
                (TestDependencyNode Node, TestDependencyNode Condition)[] conditionalDependencies = null,
                bool interestingForDynamicDependencyAnalysis = false)
            {
                _name = name;
                _markingThreadIds = markingThreadIds;
                _staticDependencies = staticDependencies ?? Array.Empty<TestDependencyNode>();
                _conditionalDependencies = conditionalDependencies ?? Array.Empty<(TestDependencyNode, TestDependencyNode)>();
                _interestingForDynamicDependencyAnalysis = interestingForDynamicDependencyAnalysis;
            }

            public override bool InterestingForDynamicDependencyAnalysis => _interestingForDynamicDependencyAnalysis;
            public override bool HasDynamicDependencies => false;
            public override bool HasConditionalStaticDependencies => _conditionalDependencies.Length != 0;
            public override bool StaticDependenciesAreComputed => true;

            public override void AddStaticDependencies(DependencySink<string> sink, string context)
            {
                foreach (TestDependencyNode dependency in _staticDependencies)
                {
                    sink.Add(dependency, "static");
                }
            }

            public override void AddConditionalDependencies(DependencySink<string> sink, string context)
            {
                foreach ((TestDependencyNode node, TestDependencyNode condition) in _conditionalDependencies)
                {
                    sink.Add(node, condition, "conditional");
                }
            }

            protected override void OnMarked(string context)
            {
                _markingThreadIds.Add(Environment.CurrentManagedThreadId);
            }

            protected override string GetName(string context) => _name;
            public override string ToString() => _name;
        }

        private sealed class DynamicTestDependencyNode : TestDependencyNode
        {
            private readonly List<int> _searchStarts;
            private readonly Dictionary<TestDependencyNode, TestDependencyNode> _dependencies;
            private bool _staticDependenciesProduced;

            public DynamicTestDependencyNode(
                string name,
                List<int> markingThreadIds,
                List<int> searchStarts,
                Dictionary<TestDependencyNode, TestDependencyNode> dependencies)
                : base(name, markingThreadIds)
            {
                _searchStarts = searchStarts;
                _dependencies = dependencies;
            }

            public override bool HasDynamicDependencies => _staticDependenciesProduced;

            public override void AddStaticDependencies(DependencySink<string> sink, string context)
            {
                _staticDependenciesProduced = true;
            }

            public override void SearchDynamicDependencies(List<DependencyNodeCore<string>> markedNodes, int firstNode, DependencySink<string> sink, string context)
            {
                _searchStarts.Add(firstNode);
                for (int i = firstNode; i < markedNodes.Count; i++)
                {
                    if (markedNodes[i] is TestDependencyNode interestingNode &&
                        _dependencies.TryGetValue(interestingNode, out TestDependencyNode dependency))
                    {
                        sink.Add(dependency, interestingNode, "dynamic");
                    }
                }
            }
        }

        private sealed class DeferredTestDependencyNode : TestDependencyNode
        {
            private readonly TestDependencyNode _dependency;
            private bool _dependenciesComputed;

            public DeferredTestDependencyNode(string name, List<int> markingThreadIds, TestDependencyNode dependency)
                : base(name, markingThreadIds)
            {
                _dependency = dependency;
            }

            public override bool StaticDependenciesAreComputed => _dependenciesComputed;

            public override bool HasDynamicDependencies
            {
                get
                {
                    Assert.True(_dependenciesComputed);
                    return false;
                }
            }

            public void SetDependenciesComputed()
            {
                _dependenciesComputed = true;
            }

            public override void AddStaticDependencies(DependencySink<string> sink, string context)
            {
                sink.Add(_dependency, "deferred");
            }
        }

        private sealed class AnalysisResult
        {
            public AnalysisResult(string[] markedNodes, int[] dynamicSearchStarts, int[] markingThreadIds)
            {
                MarkedNodes = markedNodes;
                DynamicSearchStarts = dynamicSearchStarts;
                MarkingThreadIds = markingThreadIds;
            }

            public string[] MarkedNodes { get; }
            public int[] DynamicSearchStarts { get; }
            public int[] MarkingThreadIds { get; }
        }

        private static MethodDesc GetMethodFromAttribute(CustomAttributeValue attr)
        {
            if (attr.NamedArguments.Length > 0)
                throw new NotImplementedException(); // TODO: parse sig and instantiation

            return ((TypeDesc)attr.FixedArguments[0].Value).GetMethod(Encoding.UTF8.GetBytes((string)attr.FixedArguments[1].Value), null);
        }
    }
}
