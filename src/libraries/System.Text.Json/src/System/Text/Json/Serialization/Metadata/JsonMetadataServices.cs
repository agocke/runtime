// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Converters;

namespace System.Text.Json.Serialization.Metadata
{
    /// <summary>
    /// Provides helpers to create and initialize metadata for JSON-serializable types.
    /// </summary>
    /// <remarks>This API is for use by the output of the System.Text.Json source generator and should not be called directly.</remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static partial class JsonMetadataServices
    {
        /// <summary>
        /// Creates metadata for a property or field.
        /// </summary>
        /// <typeparam name="T">The type that the converter for the property returns or accepts when converting JSON data.</typeparam>
        /// <param name="options">The <see cref="JsonSerializerOptions"/> to initialize the metadata with.</param>
        /// <param name="propertyInfo">Provides serialization metadata about the property or field.</param>
        /// <returns>A <see cref="JsonPropertyInfo"/> instance initialized with the provided metadata.</returns>
        /// <remarks>This API is for use by the output of the System.Text.Json source generator and should not be called directly.</remarks>
        public static JsonPropertyInfo CreatePropertyInfo<T>(JsonSerializerOptions options, JsonPropertyInfoValues<T> propertyInfo)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(propertyInfo);

            Type? declaringType = propertyInfo.DeclaringType;
            if (declaringType == null)
            {
                throw new ArgumentException(nameof(propertyInfo.DeclaringType));
            }

            string? propertyName = propertyInfo.PropertyName;
            if (propertyName is null)
            {
                throw new ArgumentException(nameof(propertyInfo.PropertyName));
            }

            if (!propertyInfo.IsProperty && propertyInfo.IsVirtual)
            {
                throw new InvalidOperationException(SR.Format(SR.FieldCannotBeVirtual, nameof(propertyInfo.IsProperty), nameof(propertyInfo.IsVirtual)));
            }

            return CreatePropertyInfoCore(propertyInfo, options);
        }

        /// <summary>
        /// Creates metadata for a complex class or struct.
        /// </summary>
        /// <param name="options">The <see cref="JsonSerializerOptions"/> to initialize the metadata with.</param>
        /// <param name="objectInfo">Provides serialization metadata about an object type with constructors, properties, and fields.</param>
        /// <typeparam name="T">The type of the class or struct.</typeparam>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or <paramref name="objectInfo"/> is null.</exception>
        /// <returns>A <see cref="JsonTypeInfo{T}"/> instance representing the class or struct.</returns>
        /// <remarks>This API is for use by the output of the System.Text.Json source generator and should not be called directly.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static JsonTypeInfo<T> CreateObjectInfo<T>(JsonSerializerOptions options, JsonObjectInfoValues<T> objectInfo) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(objectInfo);

            return CreateCore(options, objectInfo);
        }

        /// <summary>
        /// Creates metadata for a union type.
        /// </summary>
        /// <param name="options">The <see cref="JsonSerializerOptions"/> to initialize the metadata with.</param>
        /// <param name="unionInfo">Provides serialization metadata about a union type and its cases.</param>
        /// <typeparam name="T">The type of the union.</typeparam>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or <paramref name="unionInfo"/> is null.</exception>
        /// <returns>A <see cref="JsonTypeInfo{T}"/> instance representing the union type.</returns>
        /// <remarks>This API is for use by the output of the System.Text.Json source generator and should not be called directly.</remarks>
        public static JsonTypeInfo<T> CreateUnionInfo<T>(JsonSerializerOptions options, JsonUnionInfoValues<T> unionInfo) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(unionInfo);
            ArgumentNullException.ThrowIfNull(unionInfo.UnionCases);

            JsonConverter<T> converter = new JsonUnionConverter<T>();
            var typeInfo = new JsonTypeInfo<T>(converter, options)
            {
                UnionConstructor = unionInfo.UnionConstructor,
                UnionDeconstructor = unionInfo.UnionDeconstructor,
                TypeClassifier = unionInfo.TypeClassifier,
            };

            foreach (JsonUnionCaseInfo caseInfo in unionInfo.UnionCases)
            {
                typeInfo.UnionCases.Add(caseInfo);
            }

            typeInfo.TypeClassifierFactory = unionInfo.TypeClassifierFactory;
            typeInfo.TypeClassifierResolutionPending = true;

            typeInfo.IsCustomized = false;
            return typeInfo;
        }

        /// <summary>
        /// Creates metadata for a primitive or a type with a custom converter.
        /// </summary>
        /// <typeparam name="T">The generic type definition.</typeparam>
        /// <returns>A <see cref="JsonTypeInfo{T}"/> instance representing the type.</returns>
        /// <remarks>This API is for use by the output of the System.Text.Json source generator and should not be called directly.</remarks>
        public static JsonTypeInfo<T> CreateValueInfo<T>(JsonSerializerOptions options, JsonConverter converter)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(converter);

            JsonTypeInfo<T> info = CreateCore<T>(converter, options);
            return info;
        }

        /// <summary>
        /// Creates metadata using a custom converter from the options or a fallback converter.
        /// </summary>
        /// <typeparam name="T">The type to create metadata for.</typeparam>
        /// <param name="options">The options to initialize the metadata with and search for custom converters.</param>
        /// <param name="fallbackConverter">The converter to use when the options contain no converter for <typeparamref name="T"/>.</param>
        /// <param name="resolver">The originating resolver of the metadata.</param>
        /// <returns>A new metadata instance bound to <paramref name="options"/> and <paramref name="resolver"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/>, <paramref name="fallbackConverter"/>, or <paramref name="resolver"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">A custom converter factory returns an invalid converter.</exception>
        /// <remarks>This API is for use by the output of the System.Text.Json source generator and should not be called directly.</remarks>
        public static JsonTypeInfo<T> CreateValueInfo<T>(JsonSerializerOptions options, JsonConverter fallbackConverter, IJsonTypeInfoResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(fallbackConverter);
            ArgumentNullException.ThrowIfNull(resolver);

            if (!TryCreateValueInfo<T>(options, out JsonTypeInfo<T>? typeInfo))
            {
                typeInfo = CreateValueInfo<T>(options, fallbackConverter);
            }

            typeInfo.OriginatingResolver = resolver;
            return typeInfo;
        }

        /// <summary>
        /// Creates metadata when the options contain a custom converter for the type.
        /// </summary>
        /// <typeparam name="T">The type to create metadata for.</typeparam>
        /// <param name="options">The options to initialize the metadata with and search for custom converters.</param>
        /// <param name="typeInfo">When this method returns, contains the metadata, or <see langword="null"/> if no custom converter was found. This parameter is treated as uninitialized.</param>
        /// <returns><see langword="true"/> if a custom converter was found; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">A custom converter factory returns an invalid converter.</exception>
        /// <remarks>This API is for use by the output of the System.Text.Json source generator and should not be called directly.</remarks>
        public static bool TryCreateValueInfo<T>(JsonSerializerOptions options, [NotNullWhen(true)] out JsonTypeInfo<T>? typeInfo)
        {
            ArgumentNullException.ThrowIfNull(options);

            JsonConverter? converter = options.ExpandConverterFactory(options.GetConverterFromList(typeof(T)), typeof(T));
            typeInfo = converter is not null ? CreateValueInfo<T>(options, converter) : null;
            return typeInfo is not null;
        }
    }
}
