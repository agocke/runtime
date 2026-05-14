// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics.Tracing
{
    /// <summary>
    /// Opaque pre-computed metadata for a TraceLogging event type.
    /// Create an instance from <see cref="TraceLoggingFieldDescriptor"/> values and cache it
    /// on your <see cref="ITraceLoggingTypeDescriptor{T}"/> implementation for reuse.
    /// </summary>
    public sealed class TraceLoggingEventMetadata
    {
        internal readonly TraceLoggingEventTypes EventTypes;

        /// <summary>
        /// Initializes a new instance of <see cref="TraceLoggingEventMetadata"/> by computing
        /// the TraceLogging metadata from the specified field descriptors.
        /// </summary>
        /// <param name="name">
        /// The default event name. Used when the caller does not provide an explicit event name.
        /// </param>
        /// <param name="fields">The field descriptors describing the event payload structure.</param>
        public TraceLoggingEventMetadata(string name, params TraceLoggingFieldDescriptor[] fields)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(fields);

            var collector = new TraceLoggingMetadataCollector();
            AddFieldsToCollector(collector, fields);

            byte[] typeMetadata = collector.GetMetadata();

            EventTypes = new TraceLoggingEventTypes(name, typeMetadata, collector.ScratchSize, collector.DataCount, collector.PinCount);
        }

        private static void AddFieldsToCollector(TraceLoggingMetadataCollector collector, TraceLoggingFieldDescriptor[] fields)
        {
            foreach (TraceLoggingFieldDescriptor field in fields)
            {
                if (field.IsArray)
                {
                    collector.AddArray(field.Name, field.DataType);
                }
                else if (field.Children.Length > 0)
                {
                    TraceLoggingMetadataCollector groupCollector = collector.AddGroup(field.Name);
                    AddFieldsToCollector(groupCollector, field.Children);
                }
                else
                {
                    AddFieldToCollector(collector, field);
                }
            }
        }

        private static void AddFieldToCollector(TraceLoggingMetadataCollector collector, TraceLoggingFieldDescriptor field)
        {
            TraceLoggingDataType coreType = (TraceLoggingDataType)((int)field.DataType & Statics.InTypeMask);

            switch (coreType)
            {
                case TraceLoggingDataType.Binary:
                case TraceLoggingDataType.CountedMbcsString:
                case TraceLoggingDataType.CountedUtf16String:
                    collector.AddBinary(field.Name, field.DataType);
                    break;
                case TraceLoggingDataType.Utf16String:
                case TraceLoggingDataType.MbcsString:
                    collector.AddNullTerminatedString(field.Name, field.DataType);
                    break;
                default:
                    collector.AddScalar(field.Name, field.DataType);
                    break;
            }
        }
    }
}
