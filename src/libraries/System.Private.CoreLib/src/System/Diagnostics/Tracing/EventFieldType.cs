// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics.Tracing
{
    /// <summary>
    /// Specifies the in-memory layout and TraceLogging encoding of an event field,
    /// replacing the need for <see cref="Type"/> in the reflection-free
    /// <see cref="EventSource.WriteEventDirect"/> pathway.
    /// </summary>
    public enum EventFieldType : byte
    {
        /// <summary>4-byte WIN32 BOOL (non-zero = true).</summary>
        Boolean = 0,
        /// <summary>Signed 8-bit integer.</summary>
        Int8,
        /// <summary>Unsigned 8-bit integer.</summary>
        UInt8,
        /// <summary>Signed 16-bit integer.</summary>
        Int16,
        /// <summary>Unsigned 16-bit integer.</summary>
        UInt16,
        /// <summary>UTF-16 character (2 bytes).</summary>
        Char,
        /// <summary>Signed 32-bit integer.</summary>
        Int32,
        /// <summary>Unsigned 32-bit integer.</summary>
        UInt32,
        /// <summary>Signed 64-bit integer.</summary>
        Int64,
        /// <summary>Unsigned 64-bit integer.</summary>
        UInt64,
        /// <summary>32-bit IEEE 754 floating point.</summary>
        Float,
        /// <summary>64-bit IEEE 754 floating point.</summary>
        Double,
        /// <summary>Null-terminated UTF-16 string.</summary>
        String,
        /// <summary>128-bit GUID.</summary>
        Guid,
        /// <summary>64-bit FILETIME (DateTime).</summary>
        DateTime,
        /// <summary>128-bit decimal.</summary>
        Decimal,
        /// <summary>Platform-sized signed integer (4 or 8 bytes).</summary>
        IntPtr,
        /// <summary>
        /// Variable-length byte array. Encoded as a 4-byte length prefix
        /// followed by the blob, consuming two <see cref="EventSource.EventData"/>
        /// entries.
        /// </summary>
        ByteArray,
    }
}
