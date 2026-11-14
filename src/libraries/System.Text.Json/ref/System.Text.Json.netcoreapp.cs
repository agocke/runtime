// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.Text.Json.Serialization.Metadata
{
    public static partial class JsonMetadataServices
    {
        public static System.Text.Json.Serialization.JsonConverter<System.DateOnly> DateOnlyConverter { get { throw null; } }
        public static System.Text.Json.Serialization.JsonConverter<System.Half> HalfConverter { get { throw null; } }
        public static System.Text.Json.Serialization.JsonConverter<System.TimeOnly> TimeOnlyConverter { get { throw null; } }

        public static System.Text.Json.Serialization.JsonConverter<System.Int128> Int128Converter { get { throw null; } }
        [System.CLSCompliantAttribute(false)]
        public static System.Text.Json.Serialization.JsonConverter<System.UInt128> UInt128Converter { get { throw null; } }
    }
}

namespace System.Text.Json
{
    public static partial class JsonSerializer
    {
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(System.IO.Pipelines.PipeReader utf8Json, bool topLevelValues, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(System.IO.Pipelines.PipeReader utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(System.IO.Stream utf8Json, bool topLevelValues, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(System.IO.Stream utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<TContext, T>(System.IO.Pipelines.PipeReader utf8Json, bool topLevelValues, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<TContext, T>(System.IO.Pipelines.PipeReader utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<TContext, T>(System.IO.Stream utf8Json, bool topLevelValues, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Collections.Generic.IAsyncEnumerable<T?> DeserializeAsyncEnumerable<TContext, T>(System.IO.Stream utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.ValueTask<T?> DeserializeAsync<T>(System.IO.Pipelines.PipeReader utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.ValueTask<T?> DeserializeAsync<T>(System.IO.Stream utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.ValueTask<T?> DeserializeAsync<TContext, T>(System.IO.Pipelines.PipeReader utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.ValueTask<T?> DeserializeAsync<TContext, T>(System.IO.Stream utf8Json, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>(System.IO.Stream utf8Json) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>(System.ReadOnlySpan<byte> utf8Json) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>([System.Diagnostics.CodeAnalysis.StringSyntaxAttribute("Json")] System.ReadOnlySpan<char> json) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>([System.Diagnostics.CodeAnalysis.StringSyntaxAttribute("Json")] string json) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>(this System.Text.Json.JsonDocument document) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>(this System.Text.Json.JsonElement element) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>(this System.Text.Json.Nodes.JsonNode? node) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<T>(ref System.Text.Json.Utf8JsonReader reader) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>(System.IO.Stream utf8Json) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>(System.ReadOnlySpan<byte> utf8Json) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>([System.Diagnostics.CodeAnalysis.StringSyntaxAttribute("Json")] System.ReadOnlySpan<char> json) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>([System.Diagnostics.CodeAnalysis.StringSyntaxAttribute("Json")] string json) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>(this System.Text.Json.JsonDocument document) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>(this System.Text.Json.JsonElement element) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>(this System.Text.Json.Nodes.JsonNode? node) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static T? Deserialize<TContext, T>(ref System.Text.Json.Utf8JsonReader reader) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsyncEnumerable<T>(System.IO.Pipelines.PipeWriter utf8Json, System.Collections.Generic.IAsyncEnumerable<T> value, bool topLevelValues = false, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsyncEnumerable<T>(System.IO.Stream utf8Json, System.Collections.Generic.IAsyncEnumerable<T> value, bool topLevelValues = false, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsyncEnumerable<TContext, T>(System.IO.Pipelines.PipeWriter utf8Json, System.Collections.Generic.IAsyncEnumerable<T> value, bool topLevelValues = false, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsyncEnumerable<TContext, T>(System.IO.Stream utf8Json, System.Collections.Generic.IAsyncEnumerable<T> value, bool topLevelValues = false, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsync<T>(System.IO.Pipelines.PipeWriter utf8Json, T value, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsync<T>(System.IO.Stream utf8Json, T value, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsync<TContext, T>(System.IO.Pipelines.PipeWriter utf8Json, T value, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        [System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute(1)]
        public static System.Threading.Tasks.Task SerializeAsync<TContext, T>(System.IO.Stream utf8Json, T value, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static System.Text.Json.JsonDocument SerializeToDocument<T>(T value) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static System.Text.Json.JsonDocument SerializeToDocument<TContext, T>(T value) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static System.Text.Json.JsonElement SerializeToElement<T>(T value) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static System.Text.Json.JsonElement SerializeToElement<TContext, T>(T value) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static System.Text.Json.Nodes.JsonNode? SerializeToNode<T>(T value) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static System.Text.Json.Nodes.JsonNode? SerializeToNode<TContext, T>(T value) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static byte[] SerializeToUtf8Bytes<T>(T value) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static byte[] SerializeToUtf8Bytes<TContext, T>(T value) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static void Serialize<T>(System.IO.Stream utf8Json, T value) where T : System.Text.Json.Serialization.IJsonSerializable<T> { }
        public static void Serialize<T>(System.Text.Json.Utf8JsonWriter writer, T value) where T : System.Text.Json.Serialization.IJsonSerializable<T> { }
        public static string Serialize<T>(T value) where T : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
        public static void Serialize<TContext, T>(System.IO.Stream utf8Json, T value) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { }
        public static void Serialize<TContext, T>(System.Text.Json.Utf8JsonWriter writer, T value) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { }
        public static string Serialize<TContext, T>(T value) where TContext : System.Text.Json.Serialization.IJsonSerializable<T> { throw null; }
    }
}

namespace System.Text.Json.Serialization
{
    public partial interface IJsonSerializable<T>
    {
        static abstract System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> GetTypeInfo();
    }
    public sealed partial class JsonSerializableAttribute : System.Text.Json.Serialization.JsonAttribute
    {
        public JsonSerializableAttribute() { }
    }
}
