// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using System.Tests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SourceGenerators.Tests;
using Xunit;
using Xunit.Abstractions;

namespace System.Text.Json.SourceGeneration.UnitTests
{
    /// <summary>
    /// Verifies the source output of the System.Text.Json source generator against
    /// checked-in baselines, normalizing line endings and trailing whitespace.
    /// <para>
    /// Baselines are organized as <c>Baselines/{TestId}/{TFM}/{HintName}.cs.txt</c> where:
    /// <list type="bullet">
    ///   <item><c>TestId</c> — the test method name (e.g. <c>SimplePoco</c>)</item>
    ///   <item><c>TFM</c> — <c>netcoreapp</c> or <c>net462</c></item>
    ///   <item><c>HintName</c> — the source generator hint name (e.g. <c>MyContext.Person.g</c>)</item>
    /// </list>
    /// Every generated file is checked — not just the type-specific one.
    /// </para>
    /// <para>
    /// To regenerate all baselines after a source generator output change:
    /// <code>
    /// set RepoRootDir=D:\repos\runtime
    /// dotnet build /p:UpdateBaselines=true
    /// dotnet test --no-build
    /// </code>
    /// Then rebuild <em>without</em> the flag and re-run tests to confirm they pass.
    /// </para>
    /// </summary>
    [ActiveIssue("https://github.com/dotnet/runtime/issues/58226", TestPlatforms.Browser)]
    [SkipOnCoreClr("https://github.com/dotnet/runtime/issues/71962", ~RuntimeConfiguration.Release)]
    [SkipOnMono("https://github.com/dotnet/runtime/issues/92467")]
    [ConditionalClass(typeof(PlatformDetection), nameof(PlatformDetection.IsNotX86Process))]
    public class SourceGeneratedOutputTests(ITestOutputHelper logger)
    {
        [Fact]
        public void SimplePoco()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Person))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Person
                    {
                        public string Name { get; set; }
                        public int Age { get; set; }
                    }
                }
                """, nameof(SimplePoco));
        }

        [Fact]
        public void ParameterizedConstructor()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Point))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Point
                    {
                        public Point(int x, int y) { X = x; Y = y; }
                        public int X { get; }
                        public int Y { get; }
                    }
                }
                """, nameof(ParameterizedConstructor));
        }

        [Fact]
        public void EnumType()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Color))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public enum Color { Red, Green, Blue }
                }
                """, nameof(EnumType));
        }

        [Fact]
        public void ListProperty()
        {
            VerifyAgainstBaseline("""
                using System.Collections.Generic;
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Order))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Order
                    {
                        public List<string> Items { get; set; }
                    }
                }
                """, nameof(ListProperty));
        }

        [Fact]
        public void DictionaryProperty()
        {
            VerifyAgainstBaseline("""
                using System.Collections.Generic;
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Settings))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Settings
                    {
                        public Dictionary<string, int> Values { get; set; }
                    }
                }
                """, nameof(DictionaryProperty));
        }

        [Fact]
        public void RecordType()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Coordinate))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public record Coordinate(double Latitude, double Longitude);
                }
                """, nameof(RecordType));
        }

        [Fact]
        public void RecursiveUnionType()
        {
            // Declared with [Union] rather than the `union` keyword: the Microsoft.CodeAnalysis
            // version this project builds against cannot parse `union`, but the generator also
            // recognizes a union by the attribute plus a public object-typed Value property,
            // and derives its cases from the public single-parameter constructors.
            //
            // These test projects run against Roslyn versions that predate union pattern matching.
            // Pin the resulting generated-code diagnostics instead of ignoring them entirely.
            JsonSourceGeneratorResult result = VerifyAgainstBaseline("""
                using System.Runtime.CompilerServices;
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Nat))]
                    internal partial class MyContext : JsonSerializerContext { }

                    [Union]
                    public readonly struct Nat
                    {
                        public Nat(bool value) => Value = value;
                        public Nat(Nat value) => Value = value;
                        public object Value { get; }
                    }
                }
                """, nameof(RecursiveUnionType), disableDiagnosticValidation: true);

            AssertLegacyCompilerDiagnostics(result, "CS0037", "CS8121");
        }

        [Fact]
        public void RecursiveNullableUnionType()
        {
            // Same as RecursiveUnionType, but the self-referential case is nullable, so it is
            // also the union's null case. The emitted root-level arm has to pattern match the
            // unwrapped Nat while still reporting typeof(Nat?) as the case type.
            JsonSourceGeneratorResult result = VerifyAgainstBaseline("""
                using System.Runtime.CompilerServices;
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Nat))]
                    internal partial class MyContext : JsonSerializerContext { }

                    [Union]
                    public readonly struct Nat
                    {
                        public Nat(bool value) => Value = value;
                        public Nat(Nat? value) => Value = value;
                        public object Value { get; }
                    }
                }
                """, nameof(RecursiveNullableUnionType), disableDiagnosticValidation: true);

            AssertLegacyCompilerDiagnostics(result, "CS0037", "CS8121");
        }

        [Fact]
        public void SubclassCaseUnionType()
        {
            // A class union is not sealed, so a case type can derive from the union itself.
            // The root-level union pattern must match its payload rather than the union instance.
            JsonSourceGeneratorResult result = VerifyAgainstBaseline("""
                using System.Runtime.CompilerServices;
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Shape))]
                    internal partial class MyContext : JsonSerializerContext { }

                    [Union]
                    public class Shape
                    {
                        public Shape(int value) => Value = value;
                        public Shape(Circle value) => Value = value;
                        protected Shape() => Value = null;

                        [JsonIgnore]
                        public object Value { get; }
                    }

                    public sealed class Circle : Shape
                    {
                        public double Radius { get; set; }
                    }
                }
                """, nameof(SubclassCaseUnionType), disableDiagnosticValidation: true);

            AssertLegacyCompilerDiagnostics(result, "CS8121");
        }

        [Fact]
        public void JsonPropertyNameAttribute()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Product))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Product
                    {
                        [JsonPropertyName("product_name")]
                        public string Name { get; set; }
                        [JsonPropertyName("unit_price")]
                        public decimal Price { get; set; }
                    }
                }
                """, nameof(JsonPropertyNameAttribute));
        }

        [Fact]
        public void ConstructorWithDefaultValues()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Config))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Config
                    {
                        public Config(string name, int retries = 3, bool enabled = true)
                        {
                            Name = name;
                            Retries = retries;
                            Enabled = enabled;
                        }
                        public string Name { get; }
                        public int Retries { get; }
                        public bool Enabled { get; }
                    }
                }
                """, nameof(ConstructorWithDefaultValues));
        }

        [Fact]
        public void NullableProperties()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Measurement))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Measurement
                    {
                        public double Value { get; set; }
                        public double? Margin { get; set; }
                        public int? Count { get; set; }
                    }
                }
                """, nameof(NullableProperties));
        }

        [Fact]
        public void ByRefConstructorParameters()
        {
            VerifyAgainstBaseline("""
                using System;
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(ByRefParams))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class ByRefParams
                    {
                        public ByRefParams(in DateTime date, ref string name, out int result)
                        {
                            Date = date;
                            Name = name;
                            result = 0;
                        }
                        public DateTime Date { get; set; }
                        public string Name { get; set; }
                        public int Result { get; set; }
                    }
                }
                """, nameof(ByRefConstructorParameters));
        }

        [Fact]
        public void OutParameterWithUnsupportedType()
        {
            VerifyAgainstBaseline("""
                using System;
                using System.Threading.Tasks;
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(OutTask))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class OutTask
                    {
                        public OutTask(string name, out Task pending)
                        {
                            Name = name;
                            pending = Task.CompletedTask;
                        }
                        public string Name { get; set; }
                    }
                }
                """, nameof(OutParameterWithUnsupportedType));
        }

        [Fact]
        public void UnsafeAccessors_PrivateProperties()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(PrivateProps))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class PrivateProps
                    {
                        [JsonInclude]
                        private string Name { get; set; }
                        [JsonInclude]
                        private int Age { get; set; }
                    }
                }
                """, nameof(UnsafeAccessors_PrivateProperties));
        }

        [Fact]
        public void UnsafeAccessors_InaccessibleConstructor()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(InaccessibleCtor))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class InaccessibleCtor
                    {
                        [JsonConstructor]
                        private InaccessibleCtor(string name, int value)
                        {
                            Name = name;
                            Value = value;
                        }
                        public string Name { get; }
                        public int Value { get; }
                    }
                }
                """, nameof(UnsafeAccessors_InaccessibleConstructor));
        }

        [Fact]
        public void UnsafeAccessors_InitOnlyProperties()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(InitOnlyProps))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class InitOnlyProps
                    {
                        public string Name { get; init; }
                        public int Count { get; init; }
                    }
                }
                """, nameof(UnsafeAccessors_InitOnlyProperties));
        }

        [Fact]
        public void UnsafeAccessors_GenericTypeInitOnlyProperties()
        {
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(GenericProps<int>))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class GenericProps<T>
                    {
                        public T Value { get; init; }
                        public string Label { get; init; }
                    }
                }
                """, nameof(UnsafeAccessors_GenericTypeInitOnlyProperties));
        }

#if NET && ROSLYN4_0_OR_GREATER
        [Fact]
        public void DirectPocoAnnotation()
        {
            // The context is private and nested in the annotated type, preserving its lexical access.
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable]
                    public partial class Person
                    {
                        public string Name { get; set; }
                        public int Age { get; set; }
                    }
                }
                """, nameof(DirectPocoAnnotation), parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));
        }

        [Fact]
        public void ContextWithIJsonSerializable()
        {
            // Same shape as SimplePoco but compiled at C# 11+, so IJsonSerializable<T>
            // implementations should be emitted on the JsonSerializerContext
            // (one per [JsonSerializable(typeof(...))] entry) in a separate
            // MyContext.IJsonSerializable.g.cs file.
            VerifyAgainstBaseline("""
                using System.Text.Json.Serialization;
                namespace TestApp
                {
                    [JsonSerializable(typeof(Person))]
                    internal partial class MyContext : JsonSerializerContext { }
                    public class Person
                    {
                        public string Name { get; set; }
                        public int Age { get; set; }
                    }
                }
                """, nameof(ContextWithIJsonSerializable), parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));
        }

        [Theory]
        [InlineData("class")]
        [InlineData("struct")]
        [InlineData("record")]
        [InlineData("record struct")]
        public void DirectAnnotations_PreserveTypeMetadataAssociations(string typeKind)
        {
            Compilation compilation = CompilationHelper.CreateCompilation($$"""
                using System.Text.Json;
                using System.Text.Json.Serialization;

                namespace Z
                {
                    [JsonSerializable]
                    public partial {{typeKind}} Model { public int Number { get; set; } }
                }
                namespace A
                {
                    [JsonSerializable]
                    public partial {{typeKind}} Model { public string Text { get; set; } }
                }
                public static class Consumer
                {
                    public static string WriteZ(Z.Model value) => JsonSerializer.Serialize(value);
                    public static string WriteA(A.Model value) => JsonSerializer.Serialize(value);
                }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            result.AssertContainsType("global::A.Model");
            result.AssertContainsType("global::Z.Model");
        }

        [Fact]
        public void DirectAnnotations_HandleNestingAndTransitiveNameCollisions()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Text.Json.Serialization;

                namespace A_B
                {
                    [JsonSerializable]
                    public partial class Model
                    {
                        public One.Extra First { get; set; }
                        public Two.Extra Second { get; set; }
                    }
                }
                namespace A
                {
                    public partial class B
                    {
                        [JsonSerializable]
                        public partial class Model { public Two.Extra Second { get; set; } }
                    }
                }
                namespace One { public class Extra { public int Number { get; set; } } }
                namespace Two { public class Extra { public string Text { get; set; } } }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            Assert.Equal(2, result.ContextGenerationSpecs.Length);
            ContextGenerationSpec context = Assert.Single(result.ContextGenerationSpecs,
                spec => spec.ContextType.FullyQualifiedName.StartsWith("global::A_B.Model.", StringComparison.Ordinal));
            Assert.Equal(2, context.GeneratedTypes.Count(type => type.TypeRef.Name == "Extra"));
            Assert.Equal(context.GeneratedTypes.Count, context.GeneratedTypes.Select(type => type.TypeInfoPropertyName).Distinct().Count());
        }

        [Fact]
        public void DirectAnnotations_UseRuntimeConverterHelpers()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial class First { public int Value { get; set; } }
                [JsonSerializable]
                public partial class Second { public int Value { get; set; } }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            Assert.DoesNotContain(result.NewCompilation.SyntaxTrees,
                tree => tree.FilePath.EndsWith("JsonSerializableContextHelpers.g.cs", StringComparison.Ordinal));

            SyntaxTree[] scalarFactories = result.NewCompilation.SyntaxTrees
                .Where(tree => tree.FilePath.EndsWith(".Int32.g.cs", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, scalarFactories.Length);
            foreach (SyntaxTree factory in scalarFactories)
            {
                Assert.Contains("JsonMetadataServices.CreateValueInfo<int>", factory.ToString());
                Assert.DoesNotContain("TryGetTypeInfoForRuntimeCustomConverter", factory.ToString());
            }

            SyntaxTree[] objectFactories = result.NewCompilation.SyntaxTrees
                .Where(tree => tree.FilePath.EndsWith(".First.g.cs", StringComparison.Ordinal) ||
                    tree.FilePath.EndsWith(".Second.g.cs", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, objectFactories.Length);
            Assert.All(objectFactories, factory => Assert.Contains("JsonMetadataServices.TryCreateValueInfo<", factory.ToString()));
        }

        [Fact]
        public void DirectAnnotations_OnMultiplePartialsGenerateOnce()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial class Model { public int Number { get; set; } }
                [JsonSerializable]
                public partial class Model { public string Text { get; set; } }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            Assert.Single(result.ContextGenerationSpecs);
            Assert.Single(result.AllGeneratedTypes, type => type.TypeRef.Name == "Model");
        }

        [Theory]
        [InlineData("class")]
        [InlineData("struct")]
        [InlineData("record")]
        [InlineData("record struct")]
        public void DirectAnnotations_PrivateMembersUseLexicalAccess(string typeKind)
        {
            Compilation compilation = CompilationHelper.CreateCompilation($$"""
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial {{typeKind}} Model
                {
                    [JsonConstructor]
                    private Model(int value) { Value = _enabled ? value : 0; }
                    [JsonInclude]
                    public int Value { get; private set; }
                    [JsonInclude]
                    private string Text { get; set; }
                    [JsonInclude]
                    private bool _enabled = true;
                }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            ContextGenerationSpec context = Assert.Single(result.ContextGenerationSpecs);
            Assert.StartsWith("global::Model.", context.ContextType.FullyQualifiedName);
            TypeGenerationSpec model = Assert.Single(context.GeneratedTypes, type => type.TypeRef.Name == "Model");
            Assert.False(model.ConstructorIsInaccessible);
            Assert.All(model.PropertyGenSpecs.Where(property => property.HasJsonInclude), property =>
            {
                Assert.True(property.CanUseGetter);
                Assert.True(property.CanUseSetter);
            });
            Assert.DoesNotContain(result.NewCompilation.SyntaxTrees,
                tree => tree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) &&
                    tree.ToString().Contains("UnsafeAccessorAttribute"));
        }

        [Fact]
        public void DirectAnnotations_PreserveRequiredUnsafeAccessors()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial class Model
                {
                    public Other Child { get; set; }
                    public int Value { get; init; }
                }
                public class Other
                {
                    [JsonInclude]
                    private int Secret { get; set; }
                }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            TypeGenerationSpec other = Assert.Single(result.AllGeneratedTypes, type => type.TypeRef.Name == "Other");
            Assert.False(Assert.Single(other.PropertyGenSpecs).CanUseGetter);
            Assert.Contains(result.NewCompilation.SyntaxTrees,
                tree => tree.ToString().Contains("Name = \"get_Secret\""));
            Assert.Contains(result.NewCompilation.SyntaxTrees,
                tree => tree.ToString().Contains("Name = \"set_Value\""));
        }

        [Fact]
        public void DirectAnnotations_IsolateAccessibilityAndTypeGraphs()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial class First { public Second Child { get; set; } }
                [JsonSerializable]
                public partial class Second
                {
                    [JsonInclude]
                    private string Secret { get; set; }
                }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            Assert.Equal(2, result.ContextGenerationSpecs.Length);
            ContextGenerationSpec first = Assert.Single(result.ContextGenerationSpecs,
                spec => spec.ContextType.FullyQualifiedName.StartsWith("global::First.", StringComparison.Ordinal));
            ContextGenerationSpec second = Assert.Single(result.ContextGenerationSpecs,
                spec => spec.ContextType.FullyQualifiedName.StartsWith("global::Second.", StringComparison.Ordinal));
            Assert.DoesNotContain(second.GeneratedTypes, type => type.TypeRef.Name == "First");
            TypeGenerationSpec childFromFirst = Assert.Single(first.GeneratedTypes, type => type.TypeRef.Name == "Second");
            TypeGenerationSpec childFromSecond = Assert.Single(second.GeneratedTypes, type => type.TypeRef.Name == "Second");
            Assert.False(Assert.Single(childFromFirst.PropertyGenSpecs).CanUseGetter);
            Assert.True(Assert.Single(childFromSecond.PropertyGenSpecs).CanUseGetter);
        }

        [Theory]
        [InlineData("public")]
        [InlineData("protected")]
        [InlineData("private")]
        public void DirectAnnotations_InheritedMembersCompile(string accessibility)
        {
            Compilation compilation = CompilationHelper.CreateCompilation($$"""
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial class Model : Base { }
                public class Base
                {
                    [JsonInclude]
                    {{accessibility}} int Value { get; set; }
                }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
        }

        [Fact]
        public void DirectAnnotations_PrivateNestedTypesCompile()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Text.Json;
                using System.Text.Json.Serialization;

                public partial class Container
                {
                    [JsonSerializable]
                    private partial class Model
                    {
                        [JsonConstructor]
                        private Model() { }
                        [JsonInclude]
                        private Detail Child { get; set; }
                        private class Detail { public int Value { get; set; } }
                        public static Model Create() => new Model();
                    }
                    public static string Write() => JsonSerializer.Serialize(Model.Create());
                }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
        }

        [Theory]
        [InlineData("__JsonSerializerContext")]
        [InlineData("__JsonSerializerContext1")]
        public void DirectAnnotations_AvoidNestedContextMemberNames(string memberName)
        {
            Compilation compilation = CompilationHelper.CreateCompilation($$"""
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial class Model : Base
                {
                    public {{(memberName == "__JsonSerializerContext" ? "new " : "")}}class {{memberName}} { }
                }
                public class Base { public int __JsonSerializerContext { get; set; } }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
            Assert.NotEqual(memberName, Assert.Single(result.ContextGenerationSpecs).ContextType.Name);
        }

        [Theory]
        [InlineData("__JsonSerializerContext")]
        [InlineData("__JsonSerializerContext1")]
        public void DirectAnnotations_DisambiguateContextAndMetadataNames(string propertyName)
        {
            Compilation compilation = CompilationHelper.CreateCompilation($$"""
                using System.Text.Json.Serialization;

                [JsonSerializable(TypeInfoPropertyName = "{{propertyName}}")]
                public partial class Model { public Types.__JsonSerializerContext Child { get; set; } }
                namespace Types { public class __JsonSerializerContext { } }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
        }

        [Fact]
        public void DirectAnnotations_ExperimentalTypesCompile()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Diagnostics.CodeAnalysis;
                using System.Text.Json.Serialization;

                [Experimental("TEST001")]
                [JsonSerializable]
                public partial class Model { public int Number { get; set; } }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
        }

        [Fact]
        public void DirectAnnotations_AvoidExistingContextName()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.Text.Json;
                using System.Text.Json.Serialization;

                [JsonSerializable]
                public partial class Model { public int Number { get; set; } }
                namespace User
                {
                    [JsonSerializable(typeof(global::Model), TypeInfoPropertyName = "Poco")]
                    public partial class Model : JsonSerializerContext { }
                }
                public static class Consumer
                {
                    public static string Write(Model value) => JsonSerializer.Serialize(value);
                }
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
        }

        [Fact]
        public void Providers_CSharp11AsyncCallsWithExplicitCancellationCompile()
        {
            Compilation compilation = CompilationHelper.CreateCompilation("""
                using System.IO;
                using System.Text.Json;
                using System.Text.Json.Serialization;
                using System.Threading;
                using System.Threading.Tasks;

                [JsonSerializable]
                public partial class Model { public int Number { get; set; } }
                public static class Consumer
                {
                    public static Task Write(Stream stream, Model value) =>
                        JsonSerializer.SerializeAsync(stream, value, CancellationToken.None);
                    public static ValueTask<Model> Read(Stream stream) =>
                        JsonSerializer.DeserializeAsync<Model>(stream, CancellationToken.None);
                    public static Task WriteUnannotated(Stream stream, int value) =>
                        JsonSerializer.SerializeAsync(stream, value);
                }
                """,
                additionalReferences: new[] { MetadataReference.CreateFromFile(typeof(System.IO.Pipelines.PipeReader).Assembly.Location) },
                parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            CompilationHelper.RunJsonSourceGenerator(compilation, logger: logger);
        }

        [Theory]
        [InlineData("public partial class Model<T> { }")]
        [InlineData("public static partial class Model { }")]
        [InlineData("public ref partial struct Model { }")]
        [InlineData("file partial class Model { }")]
        public void DirectAnnotations_UnsupportedTypesReportDiagnostics(string declaration)
        {
            Compilation compilation = CompilationHelper.CreateCompilation($$"""
                using System.Text.Json.Serialization;
                [JsonSerializable]
                {{declaration}}
                """, parseOptions: CompilationHelper.CreateParseOptions((LanguageVersion)1100));

            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, disableDiagnosticValidation: true, logger: logger);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SYSLIB1030");
            Assert.DoesNotContain(result.NewCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }
#endif

        #region Baseline comparison infrastructure

        private static readonly string s_baselinesRelativePath = IO.Path.Combine(
            "src", "libraries", "System.Text.Json", "tests",
            "System.Text.Json.SourceGeneration.Unit.Tests", "Baselines");

        private static readonly string s_tfmSubFolder =
#if NET
            "netcoreapp";
#else
            "net462";
#endif

        /// <summary>
        /// Runs the source generator on <paramref name="source"/> and verifies that every
        /// generated file matches the corresponding baseline in
        /// <c>Baselines/{testId}/{tfm}/{hintName}.cs.txt</c>.
        /// </summary>
        private JsonSourceGeneratorResult VerifyAgainstBaseline(string source, string testId, bool disableDiagnosticValidation = false, CSharpParseOptions? parseOptions = null)
        {
            Compilation compilation = CompilationHelper.CreateCompilation(source, parseOptions: parseOptions);
            JsonSourceGeneratorResult result = CompilationHelper.RunJsonSourceGenerator(compilation, disableDiagnosticValidation: disableDiagnosticValidation, logger: logger);

            var inputPaths = new HashSet<string>(compilation.SyntaxTrees.Select(t => t.FilePath));
            List<SyntaxTree> generatedTrees = result.NewCompilation.SyntaxTrees
                .Where(t => !inputPaths.Contains(t.FilePath))
                .ToList();

            Assert.True(generatedTrees.Count > 0, "Source generator produced no output.");

            string baselineDir = IO.Path.Combine("Baselines", testId, s_tfmSubFolder);

            string[] actualFiles = generatedTrees
                .Select(t => ToBaselineFileName(t.FilePath))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

#if UPDATE_BASELINES
            {
                const string envVarName = "RepoRootDir";
                string? repoRootDir = Environment.GetEnvironmentVariable(envVarName);
                Assert.True(repoRootDir is not null,
                    $"To update baselines, set the '{envVarName}' environment variable to the repo root.");

                string absDir = IO.Path.Combine(repoRootDir, s_baselinesRelativePath, testId, s_tfmSubFolder);
                IO.Directory.CreateDirectory(absDir);

                // Remove stale baselines that are no longer generated.
                if (IO.Directory.Exists(absDir))
                {
                    foreach (string existing in IO.Directory.GetFiles(absDir, "*.cs.txt"))
                    {
                        string name = IO.Path.GetFileName(existing);
                        if (!actualFiles.Contains(name, StringComparer.Ordinal))
                        {
                            IO.File.Delete(existing);
                        }
                    }
                }

                foreach (SyntaxTree tree in generatedTrees)
                {
                    string baselineFileName = ToBaselineFileName(tree.FilePath);
                    string generatedSourceText = NormalizeSnapshot(tree.GetText().ToString());
                    string absPath = IO.Path.Combine(absDir, baselineFileName);
                    IO.File.WriteAllText(absPath, generatedSourceText);
                }

                return result;
            }
#else
            // Collect expected baseline files from disk.
            Assert.True(IO.Directory.Exists(baselineDir),
                $"Baseline directory not found: {baselineDir}. Build with /p:UpdateBaselines=true to generate baselines.");

            string[] expectedFiles = IO.Directory.GetFiles(baselineDir, "*.cs.txt")
                .Select(f => IO.Path.GetFileName(f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            // Verify that the set of generated files matches the set of baselines.
            Assert.True(
                expectedFiles.SequenceEqual(actualFiles, StringComparer.Ordinal),
                $"Generated file set mismatch.\nExpected: [{string.Join(", ", expectedFiles)}]\nActual:   [{string.Join(", ", actualFiles)}]");

            // Verify content of each generated file.
            foreach (SyntaxTree tree in generatedTrees)
            {
                string baselineFileName = ToBaselineFileName(tree.FilePath);
                string baselinePath = IO.Path.Combine(baselineDir, baselineFileName);
                SourceText generatedSourceText = SourceText.From(NormalizeSnapshot(tree.GetText().ToString()));

                string baseline = NormalizeSnapshot(IO.File.ReadAllText(baselinePath));
                string[] expectedLines = baseline
                    .Replace("%VERSION%", typeof(JsonSourceGenerator).Assembly.GetName().Version?.ToString())
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.None);

                bool matches = RoslynTestUtils.CompareLines(expectedLines, generatedSourceText, out string errorMessage);
                Assert.True(matches, $"Baseline mismatch for {baselineFileName}.\n{errorMessage}");
            }

            return result;
#endif
        }

        private static string NormalizeSnapshot(string source)
            => string.Join(Environment.NewLine, LineEndingsHelper.Normalize(source)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.None)
                .Select(static line => line.TrimEnd()));

        private static void AssertLegacyCompilerDiagnostics(JsonSourceGeneratorResult result, params string[] expectedIds)
        {
#if NET
            string[] actualIds = result.NewCompilation.GetDiagnostics()
                .Where(diagnostic =>
                    diagnostic.Severity is DiagnosticSeverity.Error &&
                    diagnostic.Location.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) is true)
                .Select(diagnostic => diagnostic.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedIds.OrderBy(id => id, StringComparer.Ordinal), actualIds);
#endif
        }

        /// <summary>
        /// Converts a generated tree file path (e.g. ending in <c>MyContext.Person.g.cs</c>)
        /// to a baseline file name (e.g. <c>MyContext.Person.g.cs.txt</c>).
        /// </summary>
        private static string ToBaselineFileName(string generatedFilePath)
            => IO.Path.GetFileName(generatedFilePath) + ".txt";

        #endregion
    }
}
