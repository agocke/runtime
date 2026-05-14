// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics.Tracing
{
    /// <summary>
    /// Describes a single field within a TraceLogging event payload.
    /// A field can be a scalar (leaf), a nested group of fields, or a variable-length array of scalars.
    /// </summary>
    public readonly struct TraceLoggingFieldDescriptor
    {
        /// <summary>
        /// Gets the name of this field.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the data type of this field. For group fields, this is <see cref="TraceLoggingDataType.Struct"/>.
        /// For array fields, this is the element type.
        /// </summary>
        public TraceLoggingDataType DataType { get; }

        /// <summary>
        /// Gets the child fields for a group field. Empty for scalar and array fields.
        /// </summary>
        public TraceLoggingFieldDescriptor[] Children { get; }

        /// <summary>
        /// Gets a value indicating whether this field is a variable-length array.
        /// </summary>
        public bool IsArray { get; }

        /// <summary>
        /// Creates a scalar field descriptor.
        /// </summary>
        /// <param name="name">The field name.</param>
        /// <param name="type">The TraceLogging data type for this field.</param>
        public TraceLoggingFieldDescriptor(string name, TraceLoggingDataType type)
        {
            ArgumentNullException.ThrowIfNull(name);
            Name = name;
            DataType = type;
            Children = [];
            IsArray = false;
        }

        /// <summary>
        /// Creates a nested group field descriptor.
        /// </summary>
        /// <param name="name">The field name.</param>
        /// <param name="children">The child fields within this group.</param>
        public TraceLoggingFieldDescriptor(string name, params TraceLoggingFieldDescriptor[] children)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(children);
            Name = name;
            DataType = TraceLoggingDataType.Struct;
            Children = children;
            IsArray = false;
        }

        /// <summary>
        /// Creates an array field descriptor.
        /// </summary>
        /// <param name="name">The field name.</param>
        /// <param name="elementType">The TraceLogging data type for each element.</param>
        /// <param name="isArray">Must be <see langword="true"/>.</param>
        public TraceLoggingFieldDescriptor(string name, TraceLoggingDataType elementType, bool isArray)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (!isArray)
            {
                throw new ArgumentException(SR.Arg_MustBeTrue, nameof(isArray));
            }
            Name = name;
            DataType = elementType;
            Children = [];
            IsArray = true;
        }
    }
}
