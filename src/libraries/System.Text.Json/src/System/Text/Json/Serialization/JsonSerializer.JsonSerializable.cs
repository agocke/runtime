// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace System.Text.Json;

public static partial class JsonSerializer
{
    /// <summary>Converts a JSON document into a <typeparamref name="T"/> using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="document">The document to convert.</param>
    /// <returns>A representation of the JSON value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static T? Deserialize<T>(this JsonDocument document) where T : IJsonSerializable<T>
        => Deserialize(document, T.GetTypeInfo());

    /// <summary>Converts a JSON document into a <typeparamref name="T"/> using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="document">The document to convert.</param>
    /// <returns>A representation of the JSON value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static T? Deserialize<TContext, T>(this JsonDocument document) where TContext : IJsonSerializable<T>
        => Deserialize(document, TContext.GetTypeInfo());

    /// <summary>Converts a JSON element into a <typeparamref name="T"/> using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="element">The element to convert.</param>
    /// <returns>A representation of the JSON value.</returns>
    public static T? Deserialize<T>(this JsonElement element) where T : IJsonSerializable<T>
        => Deserialize(element, T.GetTypeInfo());

    /// <summary>Converts a JSON element into a <typeparamref name="T"/> using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="element">The element to convert.</param>
    /// <returns>A representation of the JSON value.</returns>
    public static T? Deserialize<TContext, T>(this JsonElement element) where TContext : IJsonSerializable<T>
        => Deserialize(element, TContext.GetTypeInfo());

    /// <summary>Converts a JSON node into a <typeparamref name="T"/> using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="node">The node to convert.</param>
    /// <returns>A representation of the JSON value.</returns>
    public static T? Deserialize<T>(this JsonNode? node) where T : IJsonSerializable<T>
        => Deserialize(node, T.GetTypeInfo());

    /// <summary>Converts a JSON node into a <typeparamref name="T"/> using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="node">The node to convert.</param>
    /// <returns>A representation of the JSON value.</returns>
    public static T? Deserialize<TContext, T>(this JsonNode? node) where TContext : IJsonSerializable<T>
        => Deserialize(node, TContext.GetTypeInfo());

    /// <summary>Converts a value into a JSON document using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <returns>A document representing the value.</returns>
    public static JsonDocument SerializeToDocument<T>(T value) where T : IJsonSerializable<T>
        => SerializeToDocument(value, T.GetTypeInfo());

    /// <summary>Converts a value into a JSON document using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <returns>A document representing the value.</returns>
    public static JsonDocument SerializeToDocument<TContext, T>(T value) where TContext : IJsonSerializable<T>
        => SerializeToDocument(value, TContext.GetTypeInfo());

    /// <summary>Converts a value into a JSON element using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <returns>An element representing the value.</returns>
    public static JsonElement SerializeToElement<T>(T value) where T : IJsonSerializable<T>
        => SerializeToElement(value, T.GetTypeInfo());

    /// <summary>Converts a value into a JSON element using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <returns>An element representing the value.</returns>
    public static JsonElement SerializeToElement<TContext, T>(T value) where TContext : IJsonSerializable<T>
        => SerializeToElement(value, TContext.GetTypeInfo());

    /// <summary>Converts a value into a JSON node using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <returns>A node representing the value.</returns>
    public static JsonNode? SerializeToNode<T>(T value) where T : IJsonSerializable<T>
        => SerializeToNode(value, T.GetTypeInfo());

    /// <summary>Converts a value into a JSON node using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="value">The value to convert.</param>
    /// <returns>A node representing the value.</returns>
    public static JsonNode? SerializeToNode<TContext, T>(T value) where TContext : IJsonSerializable<T>
        => SerializeToNode(value, TContext.GetTypeInfo());

    /// <summary>Asynchronously reads a JSON value from a pipe using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>A task representing the deserialized value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static ValueTask<T?> DeserializeAsync<T>(PipeReader utf8Json, CancellationToken cancellationToken = default)
        where T : IJsonSerializable<T>
        => DeserializeAsync(utf8Json, T.GetTypeInfo(), cancellationToken);

    /// <summary>Asynchronously reads a JSON value from a pipe using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>A task representing the deserialized value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static ValueTask<T?> DeserializeAsync<TContext, T>(PipeReader utf8Json, CancellationToken cancellationToken = default)
        where TContext : IJsonSerializable<T>
        => DeserializeAsync(utf8Json, TContext.GetTypeInfo(), cancellationToken);

    /// <summary>Asynchronously writes a JSON value to a pipe using its default serialization metadata.</summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="utf8Json">The destination pipe.</param>
    /// <param name="value">The value to convert.</param>
    /// <param name="cancellationToken">The token used to cancel the write operation.</param>
    /// <returns>A task representing the write operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static Task SerializeAsync<T>(PipeWriter utf8Json, T value, CancellationToken cancellationToken = default)
        where T : IJsonSerializable<T>
        => SerializeAsync(utf8Json, value, T.GetTypeInfo(), cancellationToken);

    /// <summary>Asynchronously writes a JSON value to a pipe using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="utf8Json">The destination pipe.</param>
    /// <param name="value">The value to convert.</param>
    /// <param name="cancellationToken">The token used to cancel the write operation.</param>
    /// <returns>A task representing the write operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static Task SerializeAsync<TContext, T>(PipeWriter utf8Json, T value, CancellationToken cancellationToken = default)
        where TContext : IJsonSerializable<T>
        => SerializeAsync(utf8Json, value, TContext.GetTypeInfo(), cancellationToken);

    /// <summary>Reads a streaming JSON array from a pipe using its element type's default serialization metadata.</summary>
    /// <typeparam name="T">The element type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>An asynchronous sequence of the array's elements.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(PipeReader utf8Json, CancellationToken cancellationToken = default)
        where T : IJsonSerializable<T>
        => DeserializeAsyncEnumerable(utf8Json, T.GetTypeInfo(), cancellationToken);

    /// <summary>Reads a streaming JSON array from a pipe using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The element type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>An asynchronous sequence of the array's elements.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<TContext, T>(PipeReader utf8Json, CancellationToken cancellationToken = default)
        where TContext : IJsonSerializable<T>
        => DeserializeAsyncEnumerable(utf8Json, TContext.GetTypeInfo(), cancellationToken);

    /// <summary>Reads a streaming sequence of JSON values using the element type's default serialization metadata.</summary>
    /// <typeparam name="T">The element type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="topLevelValues"><see langword="true"/> to read top-level values; otherwise, <see langword="false"/> to read an array.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>An asynchronous sequence of deserialized values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(PipeReader utf8Json, bool topLevelValues, CancellationToken cancellationToken = default)
        where T : IJsonSerializable<T>
        => DeserializeAsyncEnumerable(utf8Json, T.GetTypeInfo(), topLevelValues, cancellationToken);

    /// <summary>Reads a streaming sequence of JSON values using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The element type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="topLevelValues"><see langword="true"/> to read top-level values; otherwise, <see langword="false"/> to read an array.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>An asynchronous sequence of deserialized values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<TContext, T>(PipeReader utf8Json, bool topLevelValues, CancellationToken cancellationToken = default)
        where TContext : IJsonSerializable<T>
        => DeserializeAsyncEnumerable(utf8Json, TContext.GetTypeInfo(), topLevelValues, cancellationToken);

    /// <summary>Reads a streaming sequence of JSON values using the element type's default serialization metadata.</summary>
    /// <typeparam name="T">The element type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="topLevelValues"><see langword="true"/> to read top-level values; otherwise, <see langword="false"/> to read an array.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>An asynchronous sequence of deserialized values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(Stream utf8Json, bool topLevelValues, CancellationToken cancellationToken = default)
        where T : IJsonSerializable<T>
        => DeserializeAsyncEnumerable(utf8Json, T.GetTypeInfo(), topLevelValues, cancellationToken);

    /// <summary>Reads a streaming sequence of JSON values using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The element type to deserialize.</typeparam>
    /// <param name="utf8Json">The JSON data to parse.</param>
    /// <param name="topLevelValues"><see langword="true"/> to read top-level values; otherwise, <see langword="false"/> to read an array.</param>
    /// <param name="cancellationToken">The token used to cancel the read operation.</param>
    /// <returns>An asynchronous sequence of deserialized values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<TContext, T>(Stream utf8Json, bool topLevelValues, CancellationToken cancellationToken = default)
        where TContext : IJsonSerializable<T>
        => DeserializeAsyncEnumerable(utf8Json, TContext.GetTypeInfo(), topLevelValues, cancellationToken);

    /// <summary>Writes a streaming sequence of JSON values using the element type's default serialization metadata.</summary>
    /// <typeparam name="T">The element type to serialize.</typeparam>
    /// <param name="utf8Json">The destination stream.</param>
    /// <param name="value">The sequence of values to serialize.</param>
    /// <param name="topLevelValues"><see langword="true"/> to write JSON Lines; otherwise, <see langword="false"/> to write an array.</param>
    /// <param name="cancellationToken">The token used to cancel the write operation.</param>
    /// <returns>A task representing the write operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static Task SerializeAsyncEnumerable<T>(Stream utf8Json, IAsyncEnumerable<T> value, bool topLevelValues = false, CancellationToken cancellationToken = default)
        where T : IJsonSerializable<T>
        => SerializeAsyncEnumerable(utf8Json, value, T.GetTypeInfo(), topLevelValues, cancellationToken);

    /// <summary>Writes a streaming sequence of JSON values using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The element type to serialize.</typeparam>
    /// <param name="utf8Json">The destination stream.</param>
    /// <param name="value">The sequence of values to serialize.</param>
    /// <param name="topLevelValues"><see langword="true"/> to write JSON Lines; otherwise, <see langword="false"/> to write an array.</param>
    /// <param name="cancellationToken">The token used to cancel the write operation.</param>
    /// <returns>A task representing the write operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static Task SerializeAsyncEnumerable<TContext, T>(Stream utf8Json, IAsyncEnumerable<T> value, bool topLevelValues = false, CancellationToken cancellationToken = default)
        where TContext : IJsonSerializable<T>
        => SerializeAsyncEnumerable(utf8Json, value, TContext.GetTypeInfo(), topLevelValues, cancellationToken);

    /// <summary>Writes a streaming sequence of JSON values using the element type's default serialization metadata.</summary>
    /// <typeparam name="T">The element type to serialize.</typeparam>
    /// <param name="utf8Json">The destination pipe.</param>
    /// <param name="value">The sequence of values to serialize.</param>
    /// <param name="topLevelValues"><see langword="true"/> to write JSON Lines; otherwise, <see langword="false"/> to write an array.</param>
    /// <param name="cancellationToken">The token used to cancel the write operation.</param>
    /// <returns>A task representing the write operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static Task SerializeAsyncEnumerable<T>(PipeWriter utf8Json, IAsyncEnumerable<T> value, bool topLevelValues = false, CancellationToken cancellationToken = default)
        where T : IJsonSerializable<T>
        => SerializeAsyncEnumerable(utf8Json, value, T.GetTypeInfo(), topLevelValues, cancellationToken);

    /// <summary>Writes a streaming sequence of JSON values using the specified metadata provider.</summary>
    /// <typeparam name="TContext">The type providing serialization metadata.</typeparam>
    /// <typeparam name="T">The element type to serialize.</typeparam>
    /// <param name="utf8Json">The destination pipe.</param>
    /// <param name="value">The sequence of values to serialize.</param>
    /// <param name="topLevelValues"><see langword="true"/> to write JSON Lines; otherwise, <see langword="false"/> to write an array.</param>
    /// <param name="cancellationToken">The token used to cancel the write operation.</param>
    /// <returns>A task representing the write operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static Task SerializeAsyncEnumerable<TContext, T>(PipeWriter utf8Json, IAsyncEnumerable<T> value, bool topLevelValues = false, CancellationToken cancellationToken = default)
        where TContext : IJsonSerializable<T>
        => SerializeAsyncEnumerable(utf8Json, value, TContext.GetTypeInfo(), topLevelValues, cancellationToken);
}
