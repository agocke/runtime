// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics.Tracing
{
    /// <summary>
    /// Pre-built, caller-cached metadata for a reflection-free event written
    /// via <see cref="EventSource.WriteEventDirect"/>. Holds everything needed
    /// to emit a manifest-based event to ETW / EventPipe and decode
    /// <see cref="EventSource.EventData"/> for <see cref="EventListener"/>
    /// consumers — without any <see cref="System.Type"/>,
    /// <see cref="System.Reflection.ParameterInfo"/>, or other reflection.
    ///
    /// Callers should construct one instance per distinct event shape and cache
    /// it for the lifetime of the <see cref="EventSource"/>. The event handle
    /// for EventPipe is lazily created on first write and stored on this object,
    /// so there is no internal caching inside <see cref="EventSource"/>.
    /// </summary>
    public sealed class EventDescriptorInfo
    {
        /// <summary>The event name emitted to ETW and EventListener.</summary>
        public string EventName { get; }

        /// <summary>The event severity level.</summary>
        public EventLevel Level { get; }

        /// <summary>The event keywords for filtering.</summary>
        public EventKeywords Keywords { get; }

        /// <summary>The event opcode (Start, Stop, Info, etc.).</summary>
        public EventOpcode Opcode { get; }

        /// <summary>Optional event tags.</summary>
        public EventTags Tags { get; }

        /// <summary>
        /// The numeric event ID. Must be a non-negative value that matches the
        /// <c>[Event(N)]</c> attribute on the corresponding method. Used to
        /// build the ETW <see cref="EventDescriptor"/> and the EventPipe handle.
        /// </summary>
        public int EventId { get; }

        /// <summary>The field descriptors, in order. Determines the decode
        /// logic for <see cref="EventListener"/> consumers.</summary>
        public EventFieldDescriptor[] Fields { get; }

#if FEATURE_PERFTRACING
        /// <summary>
        /// Lazily-created EventPipe event handle. Written once by
        /// <see cref="EventSource.WriteEventDirect"/> via
        /// <see cref="System.Threading.Interlocked.CompareExchange(ref nint, nint, nint)"/>
        /// and reused thereafter.
        /// </summary>
        internal IntPtr EventPipeEventHandle;
#endif

        /// <summary>
        /// Initializes a new <see cref="EventDescriptorInfo"/>.
        /// </summary>
        /// <param name="eventName">The event name. Must not be null or empty.</param>
        /// <param name="level">Severity level.</param>
        /// <param name="keywords">Filtering keywords.</param>
        /// <param name="fields">
        /// Ordered field descriptors. Must not be null.
        /// An empty array is valid (events with no payload).
        /// </param>
        /// <param name="opcode">Event opcode. Defaults to <see cref="EventOpcode.Info"/>.</param>
        /// <param name="tags">Event tags. Defaults to <see cref="EventTags.None"/>.</param>
        /// <param name="eventId">
        /// Numeric event ID matching the <c>[Event(N)]</c> attribute.
        /// Must be non-negative when used with <see cref="EventSource.WriteEventDirect"/>.
        /// Defaults to <c>-1</c>.
        /// </param>
        public EventDescriptorInfo(
            string eventName,
            EventLevel level,
            EventKeywords keywords,
            EventFieldDescriptor[] fields,
            EventOpcode opcode = EventOpcode.Info,
            EventTags tags = EventTags.None,
            int eventId = -1)
        {
            ArgumentNullException.ThrowIfNull(eventName);
            ArgumentNullException.ThrowIfNull(fields);

            EventName = eventName;
            Level = level;
            Keywords = keywords;
            Opcode = opcode;
            Tags = tags;
            EventId = eventId;
            Fields = fields;
        }


#if FEATURE_PERFTRACING
        /// <summary>
        /// Generates the EventPipe metadata blob for <see cref="EventPipeEventProvider.DefineEventHandle"/>.
        /// Uses <see cref="EventId"/> as the event identifier in the metadata.
        /// </summary>
        internal unsafe byte[] GenerateEventPipeMetadata()
        {
            // V1 header: eventID(4) + eventName(UTF-16LE null-term) + keywords(8) + version(4) + level(4) + paramCount(4)
            uint v1HeaderLen = 24 + ((uint)EventName.Length + 1) * 2;
            uint paramMetaLen = 0;

            foreach (EventFieldDescriptor f in Fields)
            {
                // Each V1 parameter: typeCode(4) + name(UTF-16LE null-term)
                paramMetaLen += 4 + ((uint)f.Name.Length + 1) * 2;
            }

            // Optional opcode tag
            uint opcodeLen = Opcode == EventOpcode.Info ? 0u : 6u;
            uint totalLen = v1HeaderLen + paramMetaLen + opcodeLen;

            byte[] metadata = new byte[totalLen];
            fixed (byte* pMetadata = metadata)
            {
                uint offset = 0;

                // eventID
                WriteUInt32(pMetadata, ref offset, (uint)EventId);

                // eventName (UTF-16LE, null-terminated)
                fixed (char* pName = EventName)
                {
                    uint nameBytes = ((uint)EventName.Length + 1) * 2;
                    Buffer.MemoryCopy(pName, pMetadata + offset, totalLen - offset, nameBytes);
                    offset += nameBytes;
                }

                // keywords
                WriteUInt64(pMetadata, ref offset, (ulong)(long)Keywords);

                // version
                WriteUInt32(pMetadata, ref offset, 0);

                // level
                WriteUInt32(pMetadata, ref offset, (uint)Level);

                // parameterCount
                WriteUInt32(pMetadata, ref offset, (uint)Fields.Length);

                // parameters
                foreach (EventFieldDescriptor f in Fields)
                {
                    WriteUInt32(pMetadata, ref offset, GetEventPipeTypeCode(f.FieldType));

                    fixed (char* pFieldName = f.Name)
                    {
                        uint fieldNameBytes = ((uint)f.Name.Length + 1) * 2;
                        Buffer.MemoryCopy(pFieldName, pMetadata + offset, totalLen - offset, fieldNameBytes);
                        offset += fieldNameBytes;
                    }
                }

                // optional opcode tag
                if (Opcode != EventOpcode.Info)
                {
                    // tag size (4 bytes) = 1 (the opcode byte)
                    WriteUInt32(pMetadata, ref offset, 1);
                    // tag kind = 1 (Opcode)
                    pMetadata[offset++] = 1;
                    // opcode value
                    pMetadata[offset++] = (byte)Opcode;
                }
            }

            return metadata;
        }

        private static unsafe void WriteUInt32(byte* buffer, ref uint offset, uint value)
        {
            *(uint*)(buffer + offset) = value;
            offset += 4;
        }

        private static unsafe void WriteUInt64(byte* buffer, ref uint offset, ulong value)
        {
            *(ulong*)(buffer + offset) = value;
            offset += 8;
        }

        /// <summary>
        /// Maps <see cref="EventFieldType"/> to the V1 EventPipe TypeCode (matching System.TypeCode values).
        /// </summary>
        private static uint GetEventPipeTypeCode(EventFieldType format) => format switch
        {
            EventFieldType.Boolean => 3,     // TypeCode.Boolean
            EventFieldType.Int8 => 5,        // TypeCode.SByte
            EventFieldType.UInt8 => 6,       // TypeCode.Byte
            EventFieldType.Int16 => 7,       // TypeCode.Int16
            EventFieldType.UInt16 => 8,      // TypeCode.UInt16
            EventFieldType.Char => 4,        // TypeCode.Char
            EventFieldType.Int32 => 9,       // TypeCode.Int32
            EventFieldType.UInt32 => 10,     // TypeCode.UInt32
            EventFieldType.Int64 => 11,      // TypeCode.Int64
            EventFieldType.UInt64 => 12,     // TypeCode.UInt64
            EventFieldType.Float => 13,      // TypeCode.Single
            EventFieldType.Double => 14,     // TypeCode.Double
            EventFieldType.String => 18,     // TypeCode.String
            EventFieldType.Guid => 17,       // not a real TypeCode, but EventPipe convention
            EventFieldType.DateTime => 16,   // TypeCode.DateTime
            EventFieldType.Decimal => 15,    // TypeCode.Decimal
            EventFieldType.IntPtr => IntPtr.Size == 8 ? 11u : 9u, // Int64 or Int32
            EventFieldType.ByteArray => 18,  // serialized as string in V1 (best-effort)
            _ => 9, // fallback to Int32
        };
#endif
    }
}
