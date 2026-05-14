// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace System.Diagnostics.Tracing
{
    /// <summary>
    /// A ref struct used to write event field values into the TraceLogging data collector.
    /// This type is a zero-field façade over the thread-static <see cref="DataCollector"/>.
    /// Each method corresponds to a TraceLogging scalar type.
    /// </summary>
    public ref struct EventDataWriter
    {
        /// <summary>Writes a <see cref="bool"/> value.</summary>
        public void WriteBoolean(bool value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(bool)); }
        }

        /// <summary>Writes a <see cref="byte"/> value.</summary>
        public void WriteByte(byte value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(byte)); }
        }

        /// <summary>Writes an <see cref="sbyte"/> value.</summary>
        [CLSCompliant(false)]
        public void WriteSByte(sbyte value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(sbyte)); }
        }

        /// <summary>Writes a <see cref="char"/> value.</summary>
        public void WriteChar(char value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(char)); }
        }

        /// <summary>Writes an <see cref="short"/> value.</summary>
        public void WriteInt16(short value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(short)); }
        }

        /// <summary>Writes a <see cref="ushort"/> value.</summary>
        [CLSCompliant(false)]
        public void WriteUInt16(ushort value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(ushort)); }
        }

        /// <summary>Writes an <see cref="int"/> value.</summary>
        public void WriteInt32(int value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(int)); }
        }

        /// <summary>Writes a <see cref="uint"/> value.</summary>
        [CLSCompliant(false)]
        public void WriteUInt32(uint value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(uint)); }
        }

        /// <summary>Writes a <see cref="long"/> value.</summary>
        public void WriteInt64(long value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(long)); }
        }

        /// <summary>Writes a <see cref="ulong"/> value.</summary>
        [CLSCompliant(false)]
        public void WriteUInt64(ulong value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(ulong)); }
        }

        /// <summary>Writes a <see cref="nint"/> value.</summary>
        public void WriteIntPtr(nint value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, nint.Size); }
        }

        /// <summary>Writes a <see cref="nuint"/> value.</summary>
        [CLSCompliant(false)]
        public void WriteUIntPtr(nuint value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, nuint.Size); }
        }

        /// <summary>Writes a <see cref="float"/> value.</summary>
        public void WriteSingle(float value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(float)); }
        }

        /// <summary>Writes a <see cref="double"/> value.</summary>
        public void WriteDouble(double value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(double)); }
        }

        /// <summary>Writes a <see cref="Guid"/> value.</summary>
        public void WriteGuid(Guid value)
        {
            unsafe { DataCollector.ThreadInstance.AddScalar(&value, sizeof(Guid)); }
        }

        /// <summary>Writes a <see cref="DateTime"/> value as a FILETIME.</summary>
        public void WriteDateTime(DateTime value)
        {
            long fileTime = value.ToFileTimeUtc();
            unsafe { DataCollector.ThreadInstance.AddScalar(&fileTime, sizeof(long)); }
        }

        /// <summary>Writes a <see cref="DateTimeOffset"/> value as a FILETIME.</summary>
        public void WriteDateTimeOffset(DateTimeOffset value)
        {
            long fileTime = value.UtcDateTime.ToFileTimeUtc();
            unsafe { DataCollector.ThreadInstance.AddScalar(&fileTime, sizeof(long)); }
        }

        /// <summary>Writes a <see cref="TimeSpan"/> value as a 64-bit tick count.</summary>
        public void WriteTimeSpan(TimeSpan value)
        {
            long ticks = value.Ticks;
            unsafe { DataCollector.ThreadInstance.AddScalar(&ticks, sizeof(long)); }
        }

        /// <summary>Writes a <see cref="decimal"/> value.</summary>
        public void WriteDecimal(decimal value)
        {
            // TraceLogging encodes decimal as Double
            double d = (double)value;
            unsafe { DataCollector.ThreadInstance.AddScalar(&d, sizeof(double)); }
        }

        /// <summary>Writes a <see cref="string"/> value as a counted UTF-16 string.</summary>
        public void WriteString(string? value)
        {
            DataCollector.ThreadInstance.AddBinary(value, value is null ? 0 : value.Length * 2);
        }

        /// <summary>Writes a blittable array value.</summary>
        /// <typeparam name="T">The unmanaged element type.</typeparam>
        public void WriteArray<T>(T[]? value) where T : unmanaged
        {
            unsafe
            {
                DataCollector.ThreadInstance.AddArray(value, value is null ? 0 : value.Length, sizeof(T));
            }
        }

        /// <summary>Marks the start of a nested group (struct) in the event payload.</summary>
        public void BeginGroup()
        {
            // Groups in TraceLogging are purely a metadata concept.
            // The data layout is flat — fields within a group are written sequentially
            // just like top-level fields. No data framing is needed here.
        }

        /// <summary>Marks the end of a nested group (struct) in the event payload.</summary>
        public void EndGroup()
        {
            // See BeginGroup — groups are metadata-only, no data framing needed.
        }
    }
}
