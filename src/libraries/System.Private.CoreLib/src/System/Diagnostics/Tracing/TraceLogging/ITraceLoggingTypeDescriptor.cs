// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics.Tracing
{
    /// <summary>
    /// Describes how to serialize a value of type <typeparamref name="T"/> into TraceLogging event fields.
    /// Implementations provide both the structural metadata (field names and types) and the write logic.
    /// </summary>
    /// <typeparam name="T">The payload type to serialize.</typeparam>
    public interface ITraceLoggingTypeDescriptor<T>
    {
        /// <summary>
        /// Gets the pre-computed event metadata for this type.
        /// This should be created once and cached for the lifetime of the descriptor.
        /// </summary>
        TraceLoggingEventMetadata Metadata { get; }

        /// <summary>
        /// Writes the fields of <paramref name="data"/> into the <paramref name="writer"/>.
        /// The fields written must match the order and types described by <see cref="Metadata"/>.
        /// </summary>
        /// <param name="writer">The writer to use for serializing field values.</param>
        /// <param name="data">The data to serialize.</param>
        void Write(EventDataWriter writer, T data);
    }
}
