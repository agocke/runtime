// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET

using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;
using Xunit;

namespace System.Text.Json.Serialization.Tests;

[JsonSerializable]
internal partial class DirectAnnotatedPoco
{
    public string? Value { get; set; }
}

public partial class JsonSerializableTests
{
    private const string Json = """{"Value":"hello"}""";

    [JsonSerializable(typeof(DirectAnnotatedPoco))]
    private partial class TestContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(DirectAnnotatedPoco))]
    private partial class CamelCaseContext : JsonSerializerContext;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SynchronousOverloads(bool useContext)
    {
        var value = new DirectAnnotatedPoco { Value = "hello" };
        Assert.Equal(Json, useContext ? JsonSerializer.Serialize<TestContext, DirectAnnotatedPoco>(value) : JsonSerializer.Serialize(value));
        byte[] bytes = useContext ? JsonSerializer.SerializeToUtf8Bytes<TestContext, DirectAnnotatedPoco>(value) : JsonSerializer.SerializeToUtf8Bytes(value);
        Assert.Equal(Json, Encoding.UTF8.GetString(bytes));

        AssertValue(useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>(Json) : JsonSerializer.Deserialize<DirectAnnotatedPoco>(Json));
        AssertValue(useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>(Json.AsSpan()) : JsonSerializer.Deserialize<DirectAnnotatedPoco>(Json.AsSpan()));
        AssertValue(useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>(bytes.AsSpan()) : JsonSerializer.Deserialize<DirectAnnotatedPoco>(bytes.AsSpan()));

        var reader = new Utf8JsonReader(bytes);
        AssertValue(useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>(ref reader) : JsonSerializer.Deserialize<DirectAnnotatedPoco>(ref reader));
        Assert.Equal(bytes.Length, reader.BytesConsumed);

        using var stream = new MemoryStream();
        if (useContext)
        {
            JsonSerializer.Serialize<TestContext, DirectAnnotatedPoco>(stream, value);
        }
        else
        {
            JsonSerializer.Serialize(stream, value);
        }

        Assert.Equal(Json, Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        AssertValue(useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>(stream) : JsonSerializer.Deserialize<DirectAnnotatedPoco>(stream));

        stream.SetLength(0);
        using var writer = new Utf8JsonWriter(stream);
        if (useContext)
        {
            JsonSerializer.Serialize<TestContext, DirectAnnotatedPoco>(writer, value);
        }
        else
        {
            JsonSerializer.Serialize(writer, value);
        }

        writer.Flush();
        Assert.Equal(Json, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DomOverloads(bool useContext)
    {
        var value = new DirectAnnotatedPoco { Value = "hello" };
        using JsonDocument document = useContext ? JsonSerializer.SerializeToDocument<TestContext, DirectAnnotatedPoco>(value) : JsonSerializer.SerializeToDocument(value);
        Assert.Equal(Json, document.RootElement.GetRawText());
        AssertValue(useContext ? document.Deserialize<TestContext, DirectAnnotatedPoco>() : document.Deserialize<DirectAnnotatedPoco>());

        JsonElement element = useContext ? JsonSerializer.SerializeToElement<TestContext, DirectAnnotatedPoco>(value) : JsonSerializer.SerializeToElement(value);
        Assert.Equal(Json, element.GetRawText());
        AssertValue(useContext ? element.Deserialize<TestContext, DirectAnnotatedPoco>() : element.Deserialize<DirectAnnotatedPoco>());

        JsonNode? node = useContext ? JsonSerializer.SerializeToNode<TestContext, DirectAnnotatedPoco>(value) : JsonSerializer.SerializeToNode(value);
        Assert.Equal("hello", node![nameof(DirectAnnotatedPoco.Value)]!.GetValue<string>());
        AssertValue(useContext ? node.Deserialize<TestContext, DirectAnnotatedPoco>() : node.Deserialize<DirectAnnotatedPoco>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AsynchronousOverloads(bool useContext, bool usePipe)
    {
        var value = new DirectAnnotatedPoco { Value = "hello" };
        using var stream = new MemoryStream();
        if (usePipe)
        {
            PipeWriter writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
            await (useContext ? JsonSerializer.SerializeAsync<TestContext, DirectAnnotatedPoco>(writer, value) : JsonSerializer.SerializeAsync(writer, value));
            await writer.CompleteAsync();
        }
        else
        {
            await (useContext ? JsonSerializer.SerializeAsync<TestContext, DirectAnnotatedPoco>(stream, value) : JsonSerializer.SerializeAsync(stream, value));
        }

        Assert.Equal(Json, Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        if (usePipe)
        {
            PipeReader reader = PipeReader.Create(stream);
            AssertValue(await (useContext ? JsonSerializer.DeserializeAsync<TestContext, DirectAnnotatedPoco>(reader) : JsonSerializer.DeserializeAsync<DirectAnnotatedPoco>(reader)));
            await reader.CompleteAsync();
        }
        else
        {
            AssertValue(await (useContext ? JsonSerializer.DeserializeAsync<TestContext, DirectAnnotatedPoco>(stream) : JsonSerializer.DeserializeAsync<DirectAnnotatedPoco>(stream)));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task StreamingOverloads(bool useContext, bool usePipe, bool topLevelValues)
    {
        using var stream = new MemoryStream();
        if (usePipe)
        {
            PipeWriter writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
            await (useContext
                ? JsonSerializer.SerializeAsyncEnumerable<TestContext, DirectAnnotatedPoco>(writer, Values(), topLevelValues)
                : JsonSerializer.SerializeAsyncEnumerable(writer, Values(), topLevelValues));
            await writer.CompleteAsync();
        }
        else
        {
            await (useContext
                ? JsonSerializer.SerializeAsyncEnumerable<TestContext, DirectAnnotatedPoco>(stream, Values(), topLevelValues)
                : JsonSerializer.SerializeAsyncEnumerable(stream, Values(), topLevelValues));
        }

        Assert.Equal(topLevelValues ? Json + "\nnull\n" : "[" + Json + ",null]", Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        PipeReader? reader = usePipe ? PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)) : null;
        IAsyncEnumerable<DirectAnnotatedPoco?> values = reader is not null
            ? useContext
                ? JsonSerializer.DeserializeAsyncEnumerable<TestContext, DirectAnnotatedPoco>(reader, topLevelValues)
                : JsonSerializer.DeserializeAsyncEnumerable<DirectAnnotatedPoco>(reader, topLevelValues)
            : useContext
                ? JsonSerializer.DeserializeAsyncEnumerable<TestContext, DirectAnnotatedPoco>(stream, topLevelValues)
                : JsonSerializer.DeserializeAsyncEnumerable<DirectAnnotatedPoco>(stream, topLevelValues);

        await Verify(values);
        if (!topLevelValues)
        {
            if (reader is not null)
            {
                await reader.CompleteAsync();
            }

            stream.Position = 0;
            reader = usePipe ? PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)) : null;
            values = reader is not null
                ? useContext
                    ? JsonSerializer.DeserializeAsyncEnumerable<TestContext, DirectAnnotatedPoco>(reader)
                    : JsonSerializer.DeserializeAsyncEnumerable<DirectAnnotatedPoco>(reader)
                : useContext
                    ? JsonSerializer.DeserializeAsyncEnumerable<TestContext, DirectAnnotatedPoco>(stream)
                    : JsonSerializer.DeserializeAsyncEnumerable<DirectAnnotatedPoco>(stream);
            await Verify(values);
        }

        if (reader is not null)
        {
            await reader.CompleteAsync();
        }

        static async IAsyncEnumerable<DirectAnnotatedPoco> Values()
        {
            yield return new DirectAnnotatedPoco { Value = "hello" };
            await Task.Yield();
            yield return null!;
        }

        static async Task Verify(IAsyncEnumerable<DirectAnnotatedPoco?> values)
        {
            var results = new List<DirectAnnotatedPoco?>();
            await foreach (DirectAnnotatedPoco? item in values)
            {
                results.Add(item);
            }

            Assert.Equal(2, results.Count);
            AssertValue(results[0]);
            Assert.Null(results[1]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullAndMalformedInputs(bool useContext)
    {
        Assert.Equal("null", useContext ? JsonSerializer.Serialize<TestContext, DirectAnnotatedPoco>(null!) : JsonSerializer.Serialize<DirectAnnotatedPoco>(null!));
        Assert.Null(useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>("null") : JsonSerializer.Deserialize<DirectAnnotatedPoco>("null"));
        Assert.Null(useContext ? ((JsonNode?)null).Deserialize<TestContext, DirectAnnotatedPoco>() : ((JsonNode?)null).Deserialize<DirectAnnotatedPoco>());
        Assert.Throws<ArgumentNullException>("json", () => useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>((string)null!) : JsonSerializer.Deserialize<DirectAnnotatedPoco>((string)null!));
        Assert.Throws<ArgumentNullException>("document", () => useContext ? ((JsonDocument)null!).Deserialize<TestContext, DirectAnnotatedPoco>() : ((JsonDocument)null!).Deserialize<DirectAnnotatedPoco>());
        Assert.Throws<JsonException>(() => useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>("{") : JsonSerializer.Deserialize<DirectAnnotatedPoco>("{"));
        Assert.Throws<JsonException>(() => useContext ? JsonSerializer.Deserialize<TestContext, DirectAnnotatedPoco>(Json + Json) : JsonSerializer.Deserialize<DirectAnnotatedPoco>(Json + Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationIsForwarded(bool useContext)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await (useContext
            ? JsonSerializer.DeserializeAsync<TestContext, DirectAnnotatedPoco>(stream, source.Token)
            : JsonSerializer.DeserializeAsync<DirectAnnotatedPoco>(stream, source.Token)));
    }

    [Fact]
    public void ProvidersReturnCachedMetadataAndPreserveContextOptions()
    {
        JsonTypeInfo<DirectAnnotatedPoco> direct = GetTypeInfo<DirectAnnotatedPoco>();
        Assert.Same(direct, GetTypeInfo<DirectAnnotatedPoco>());
        Assert.Same(TestContext.Default.DirectAnnotatedPoco, GetTypeInfo<TestContext>());
        Assert.Same(CamelCaseContext.Default.DirectAnnotatedPoco, GetTypeInfo<CamelCaseContext>());
        Assert.Equal("""{"value":"hello"}""", JsonSerializer.Serialize<CamelCaseContext, DirectAnnotatedPoco>(new() { Value = "hello" }));

        static JsonTypeInfo<DirectAnnotatedPoco> GetTypeInfo<TProvider>() where TProvider : IJsonSerializable<DirectAnnotatedPoco>
            => TProvider.GetTypeInfo();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectAnnotations_PrivateMembersRoundtrip(bool useStruct)
    {
        const string ExpectedJson = """{"Value":42,"Text":"private","_count":7}""";
        if (useStruct)
        {
            Verify(PrivateMembersStruct.Create(), value => value.GetValues());
        }
        else
        {
            Verify(PrivateMembersClass.Create(), value => value.GetValues());
        }

        static void Verify<T>(T value, Func<T, (int, string?, int)> getValues) where T : IJsonSerializable<T>
        {
            JsonTypeInfo<T> typeInfo = T.GetTypeInfo();
            Assert.NotNull(typeInfo.SerializeHandler);
            Assert.Equal(ExpectedJson, JsonSerializer.Serialize(value));
            Assert.Equal((42, "private", 7), getValues(JsonSerializer.Deserialize<T>(ExpectedJson)!));

            using var stream = new MemoryStream();
            JsonSerializer.Serialize(stream, value);
            Assert.Equal(ExpectedJson, Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    [JsonSerializable]
    private partial class PrivateMembersClass
    {
        [JsonConstructor]
        private PrivateMembersClass(int value) => Value = value;

        [JsonInclude]
        public int Value { get; private set; }
        [JsonInclude]
        private string? Text { get; set; }
        [JsonInclude]
        private int _count;

        public static PrivateMembersClass Create() => new(42) { Text = "private", _count = 7 };
        public (int, string?, int) GetValues() => (Value, Text, _count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectAnnotations_PrimitiveMetadataPreservesOptions(bool useStruct)
    {
        if (useStruct)
        {
            Verify<PrivateMembersStruct>();
        }
        else
        {
            Verify<PrivateMembersClass>();
        }

        static void Verify<T>() where T : IJsonSerializable<T>
        {
            JsonTypeInfo<T> root = T.GetTypeInfo();
            var original = (JsonTypeInfo<int>)root.Options.GetTypeInfo(typeof(int));
            var options = new JsonSerializerOptions { TypeInfoResolver = root.OriginatingResolver };
            options.Converters.Add(new ScaledIntConverterFactory());
            var customized = (JsonTypeInfo<int>)options.GetTypeInfo(typeof(int));

            Assert.NotSame(original, customized);
            Assert.Same(root.Options, original.Options);
            Assert.Same(options, customized.Options);
            Assert.Same(root.OriginatingResolver, original.OriginatingResolver);
            Assert.Same(root.OriginatingResolver, customized.OriginatingResolver);
            Assert.Equal("84", JsonSerializer.Serialize(42, customized));
            Assert.Equal(42, JsonSerializer.Deserialize("84", customized));
            Assert.Equal("42", JsonSerializer.Serialize(42, original));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectAnnotations_PrivatePropertyConverterFactoriesRoundtrip(bool useNull)
    {
        var value = new PropertyConverters { Value = 42, Optional = useNull ? null : 63 };
        string expected = useNull ? """{"Value":84,"Optional":null}""" : """{"Value":84,"Optional":126}""";
        Assert.Equal(expected, JsonSerializer.Serialize(value));
        PropertyConverters copy = JsonSerializer.Deserialize<PropertyConverters>(expected)!;
        Assert.Equal(42, copy.Value);
        Assert.Equal(value.Optional, copy.Optional);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataHelpers_CustomConvertersRespectOrder(bool useFactory)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        Assert.False(JsonMetadataServices.TryCreateValueInfo<int>(options, out JsonTypeInfo<int>? missing));
        Assert.Null(missing);

        options.Converters.Add(useFactory ? new ScaledIntConverterFactory() : new ScaledIntConverter());
        options.Converters.Add(JsonMetadataServices.Int32Converter);
        Assert.True(JsonMetadataServices.TryCreateValueInfo<int>(options, out JsonTypeInfo<int>? custom));
        Assert.Same(options, custom.Options);
        Assert.Equal("84", JsonSerializer.Serialize(42, custom));
        Assert.Equal(42, JsonSerializer.Deserialize("84", custom));

        JsonTypeInfo<int> explicitConverter = JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);
        Assert.Equal("42", JsonSerializer.Serialize(42, explicitConverter));

        var resolver = new DefaultJsonTypeInfoResolver();
        JsonTypeInfo<int> resolved = JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter, resolver);
        Assert.Same(options, resolved.Options);
        Assert.Same(resolver, resolved.OriginatingResolver);
        Assert.Equal("84", JsonSerializer.Serialize(42, resolved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataHelpers_NullableConverters(bool handlesNullable)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        JsonConverter converter = handlesNullable ? new NullableScaledIntConverter() : new ScaledIntConverterFactory();
        JsonConverter<int?> nullableConverter = JsonMetadataServices.GetNullableConverter<int>(converter, options);
        JsonTypeInfo<int?> typeInfo = JsonMetadataServices.CreateValueInfo<int?>(options, nullableConverter);

        Assert.Equal("84", JsonSerializer.Serialize((int?)42, typeInfo));
        Assert.Equal(42, JsonSerializer.Deserialize("84", typeInfo));
        Assert.Equal("null", JsonSerializer.Serialize((int?)null, typeInfo));
        Assert.Null(JsonSerializer.Deserialize("null", typeInfo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataHelpers_RejectInvalidFactories(bool returnsFactory)
    {
        var factory = new InvalidIntConverterFactory(returnsFactory);
        var options = new JsonSerializerOptions { Converters = { factory } };
        Assert.Throws<InvalidOperationException>(() => JsonMetadataServices.TryCreateValueInfo<int>(options, out _));
        Assert.Throws<InvalidOperationException>(() => JsonMetadataServices.ExpandConverter(typeof(int), factory, options));
        Assert.Throws<InvalidOperationException>(() => JsonMetadataServices.GetNullableConverter<int>(factory, options));
    }

    [Fact]
    public void MetadataHelpers_RejectIncompatibleConverters()
    {
        var options = new JsonSerializerOptions();
        Assert.Throws<InvalidOperationException>(() => JsonMetadataServices.ExpandConverter(typeof(int), JsonMetadataServices.StringConverter, options));
        Assert.Throws<InvalidOperationException>(() => JsonMetadataServices.GetNullableConverter<int>(JsonMetadataServices.StringConverter, options));
    }

    [Fact]
    public void MetadataHelpers_RejectNullArguments()
    {
        var options = new JsonSerializerOptions();
        JsonConverter converter = JsonMetadataServices.Int32Converter;
        var resolver = new DefaultJsonTypeInfoResolver();
        Assert.Throws<ArgumentNullException>("options", () => JsonMetadataServices.TryCreateValueInfo<int>(null!, out _));
        Assert.Throws<ArgumentNullException>("options", () => JsonMetadataServices.CreateValueInfo<int>(null!, converter, resolver));
        Assert.Throws<ArgumentNullException>("fallbackConverter", () => JsonMetadataServices.CreateValueInfo<int>(options, null!, resolver));
        Assert.Throws<ArgumentNullException>("resolver", () => JsonMetadataServices.CreateValueInfo<int>(options, converter, null!));
        Assert.Throws<ArgumentNullException>("typeToConvert", () => JsonMetadataServices.ExpandConverter(null!, converter, options));
        Assert.Throws<ArgumentNullException>("converter", () => JsonMetadataServices.ExpandConverter(typeof(int), null!, options));
        Assert.Throws<ArgumentNullException>("options", () => JsonMetadataServices.ExpandConverter(typeof(int), converter, null!));
        Assert.Throws<ArgumentNullException>("converter", () => JsonMetadataServices.GetNullableConverter<int>(null!, options));
        Assert.Throws<ArgumentNullException>("options", () => JsonMetadataServices.GetNullableConverter<int>(converter, null!));
    }

    [JsonSerializable]
    private partial class PropertyConverters
    {
        [JsonConverter(typeof(ScaledIntConverterFactory))]
        public int Value { get; set; }

        [JsonConverter(typeof(ScaledIntConverterFactory))]
        public int? Optional { get; set; }
    }

    private sealed class ScaledIntConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(int);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => new ScaledIntConverter();
    }

    private sealed class ScaledIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetInt32() / 2;
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value * 2);
    }

    private sealed class NullableScaledIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType is JsonTokenType.Null ? null : reader.GetInt32() / 2;

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value is int number)
            {
                writer.WriteNumberValue(number * 2);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    private sealed class InvalidIntConverterFactory(bool returnsFactory) : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(int) || typeToConvert == typeof(int?);
        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) => returnsFactory ? this : null;
    }

    [JsonSerializable]
    private partial struct PrivateMembersStruct
    {
        [JsonConstructor]
        private PrivateMembersStruct(int value) => Value = value;

        [JsonInclude]
        public int Value { get; private set; }
        [JsonInclude]
        private string? Text { get; set; }
        [JsonInclude]
        private int _count;

        public static PrivateMembersStruct Create() => new(42) { Text = "private", _count = 7 };
        public readonly (int, string?, int) GetValues() => (Value, Text, _count);
    }

    [Fact]
    public void DynamicCallsRequireExplicitOptions()
    {
        dynamic value = new ExpandoObject();
        value.Value = "hello";
        Assert.Throws<RuntimeBinderException>(() => JsonSerializer.Serialize(value));
        Assert.Equal(Json, JsonSerializer.Serialize(value, (JsonSerializerOptions?)null));
    }

    private static void AssertValue(DirectAnnotatedPoco? value)
    {
        Assert.NotNull(value);
        Assert.Equal("hello", value.Value);
    }
}

#endif
