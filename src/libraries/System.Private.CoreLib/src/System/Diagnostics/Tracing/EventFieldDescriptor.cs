// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics.Tracing
{
    /// <summary>
    /// Describes one field (parameter) of an event for the reflection-free
    /// <see cref="EventSource.WriteEventDirect"/> pathway.
    /// Pairs a field name with its <see cref="EventFieldType"/>.
    /// </summary>
    public readonly struct EventFieldDescriptor
    {
        /// <summary>The name of the field, emitted to listeners and TraceLogging metadata.</summary>
        public string Name { get; }

        /// <summary>The wire format / in-memory layout of the field.</summary>
        public EventFieldType FieldType { get; }

        /// <summary>
        /// Initializes a new <see cref="EventFieldDescriptor"/>.
        /// </summary>
        /// <param name="name">The field name. Must not be null.</param>
        /// <param name="fieldType">The field type.</param>
        public EventFieldDescriptor(string name, EventFieldType fieldType)
        {
            ArgumentNullException.ThrowIfNull(name);
            Name = name;
            FieldType = fieldType;
        }
    }
}
