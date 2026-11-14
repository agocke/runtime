// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SourceGenerators;

namespace System.Text.Json.SourceGeneration
{
    /// <summary>
    /// Represents a POCO type annotated with <c>[JsonSerializable]</c> (parameterless)
    /// that needs an <c>IJsonSerializable&lt;T&gt;</c> implementation generated.
    /// </summary>
    public sealed record PocoSerializableSpec
    {
        public required ContextGenerationSpec ContextSpec { get; init; }

        /// <summary>
        /// The POCO type reference.
        /// </summary>
        public required TypeRef TypeRef { get; init; }

        /// <summary>
        /// The property name on the nested context that returns the <c>JsonTypeInfo&lt;T&gt;</c>.
        /// </summary>
        public required string TypeInfoPropertyName { get; init; }

        /// <summary>
        /// The type declarations for the POCO type (including containing types),
        /// used to emit partial class declarations.
        /// </summary>
        public required ImmutableEquatableArray<string> TypeDeclarations { get; init; }
    }
}
