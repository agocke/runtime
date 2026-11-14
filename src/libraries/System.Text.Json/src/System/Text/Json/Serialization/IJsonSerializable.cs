// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization.Metadata;

namespace System.Text.Json.Serialization
{
    /// <summary>
    /// Provides access to default <see cref="JsonTypeInfo{T}"/> metadata, enabling type-safe,
    /// AOT-compatible JSON serialization without explicit context plumbing.
    /// </summary>
    /// <typeparam name="T">The type that serialization metadata is provided for.</typeparam>
    public interface IJsonSerializable<T>
    {
        /// <summary>
        /// Gets the default <see cref="JsonTypeInfo{T}"/> for this type.
        /// The returned instance is pre-built, cached, and bound to the source-generated default options.
        /// </summary>
        /// <returns>The default <see cref="JsonTypeInfo{T}"/> instance.</returns>
        static abstract JsonTypeInfo<T> GetTypeInfo();
    }
}
