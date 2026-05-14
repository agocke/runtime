// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.Tracing;
using Xunit;

namespace BasicEventSourceTests
{
    public class TestsWriteDescriptor
    {
        private sealed class SimplePayload
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }

        private sealed class SimplePayloadDescriptor : ITraceLoggingTypeDescriptor<SimplePayload>
        {
            public static readonly SimplePayloadDescriptor Instance = new();

            public TraceLoggingEventMetadata Metadata { get; } = new("SimplePayload",
                new TraceLoggingFieldDescriptor("Id", TraceLoggingDataType.Int32),
                new TraceLoggingFieldDescriptor("Name", TraceLoggingDataType.CountedUtf16String));

            public void Write(EventDataWriter writer, SimplePayload data)
            {
                writer.WriteInt32(data.Id);
                writer.WriteString(data.Name);
            }
        }

        private sealed class AllScalarsPayload
        {
            public bool BoolVal { get; set; }
            public byte ByteVal { get; set; }
            public sbyte SByteVal { get; set; }
            public char CharVal { get; set; }
            public short Int16Val { get; set; }
            public ushort UInt16Val { get; set; }
            public int Int32Val { get; set; }
            public uint UInt32Val { get; set; }
            public long Int64Val { get; set; }
            public ulong UInt64Val { get; set; }
            public float SingleVal { get; set; }
            public double DoubleVal { get; set; }
            public Guid GuidVal { get; set; }
        }

        private sealed class AllScalarsDescriptor : ITraceLoggingTypeDescriptor<AllScalarsPayload>
        {
            public static readonly AllScalarsDescriptor Instance = new();

            public TraceLoggingEventMetadata Metadata { get; } = new("AllScalarsPayload",
                new TraceLoggingFieldDescriptor("BoolVal", TraceLoggingDataType.Boolean8),
                new TraceLoggingFieldDescriptor("ByteVal", TraceLoggingDataType.UInt8),
                new TraceLoggingFieldDescriptor("SByteVal", TraceLoggingDataType.Int8),
                new TraceLoggingFieldDescriptor("CharVal", TraceLoggingDataType.Char16),
                new TraceLoggingFieldDescriptor("Int16Val", TraceLoggingDataType.Int16),
                new TraceLoggingFieldDescriptor("UInt16Val", TraceLoggingDataType.UInt16),
                new TraceLoggingFieldDescriptor("Int32Val", TraceLoggingDataType.Int32),
                new TraceLoggingFieldDescriptor("UInt32Val", TraceLoggingDataType.UInt32),
                new TraceLoggingFieldDescriptor("Int64Val", TraceLoggingDataType.Int64),
                new TraceLoggingFieldDescriptor("UInt64Val", TraceLoggingDataType.UInt64),
                new TraceLoggingFieldDescriptor("SingleVal", TraceLoggingDataType.Float),
                new TraceLoggingFieldDescriptor("DoubleVal", TraceLoggingDataType.Double),
                new TraceLoggingFieldDescriptor("GuidVal", TraceLoggingDataType.Guid));

            public void Write(EventDataWriter writer, AllScalarsPayload data)
            {
                writer.WriteBoolean(data.BoolVal);
                writer.WriteByte(data.ByteVal);
                writer.WriteSByte(data.SByteVal);
                writer.WriteChar(data.CharVal);
                writer.WriteInt16(data.Int16Val);
                writer.WriteUInt16(data.UInt16Val);
                writer.WriteInt32(data.Int32Val);
                writer.WriteUInt32(data.UInt32Val);
                writer.WriteInt64(data.Int64Val);
                writer.WriteUInt64(data.UInt64Val);
                writer.WriteSingle(data.SingleVal);
                writer.WriteDouble(data.DoubleVal);
                writer.WriteGuid(data.GuidVal);
            }
        }

        private sealed class NestedAddress
        {
            public string? City { get; set; }
            public string? State { get; set; }
        }

        private sealed class OrderPayload
        {
            public int OrderId { get; set; }
            public NestedAddress? Shipping { get; set; }
        }

        private sealed class OrderDescriptor : ITraceLoggingTypeDescriptor<OrderPayload>
        {
            public static readonly OrderDescriptor Instance = new();

            public TraceLoggingEventMetadata Metadata { get; } = new("OrderPayload",
                new TraceLoggingFieldDescriptor("OrderId", TraceLoggingDataType.Int32),
                new TraceLoggingFieldDescriptor("Shipping",
                    new TraceLoggingFieldDescriptor("City", TraceLoggingDataType.CountedUtf16String),
                    new TraceLoggingFieldDescriptor("State", TraceLoggingDataType.CountedUtf16String)));

            public void Write(EventDataWriter writer, OrderPayload data)
            {
                writer.WriteInt32(data.OrderId);
                writer.BeginGroup();
                writer.WriteString(data.Shipping?.City);
                writer.WriteString(data.Shipping?.State);
                writer.EndGroup();
            }
        }

        private sealed class ArrayPayload
        {
            public int[]? Values { get; set; }
        }

        private sealed class ArrayPayloadDescriptor : ITraceLoggingTypeDescriptor<ArrayPayload>
        {
            public static readonly ArrayPayloadDescriptor Instance = new();

            public TraceLoggingEventMetadata Metadata { get; } = new("ArrayPayload",
                new TraceLoggingFieldDescriptor("Values", TraceLoggingDataType.Int32, isArray: true));

            public void Write(EventDataWriter writer, ArrayPayload data)
            {
                writer.WriteArray(data.Values);
            }
        }

        [Fact]
        public void Write_SimplePayload_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_Simple");
            var payload = new SimplePayload { Id = 42, Name = "Hello" };
            es.Write("SimpleEvent", in payload, SimplePayloadDescriptor.Instance);
        }

        [Fact]
        public void Write_WithOptions_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_Options");
            var payload = new SimplePayload { Id = 1, Name = "Test" };
            var options = new EventSourceOptions { Level = EventLevel.Warning };
            es.Write("OptionsEvent", options, in payload, SimplePayloadDescriptor.Instance);
        }

        [Fact]
        public void Write_AllScalars_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_AllScalars");
            var payload = new AllScalarsPayload
            {
                BoolVal = true,
                ByteVal = 0xFF,
                SByteVal = -1,
                CharVal = 'Z',
                Int16Val = -100,
                UInt16Val = 200,
                Int32Val = int.MaxValue,
                UInt32Val = uint.MaxValue,
                Int64Val = long.MinValue,
                UInt64Val = ulong.MaxValue,
                SingleVal = 3.14f,
                DoubleVal = 2.71828,
                GuidVal = Guid.NewGuid(),
            };
            es.Write("AllScalarsEvent", in payload, AllScalarsDescriptor.Instance);
        }

        [Fact]
        public void Write_NestedGroup_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_Nested");
            var payload = new OrderPayload
            {
                OrderId = 1001,
                Shipping = new NestedAddress { City = "Seattle", State = "WA" },
            };
            es.Write("OrderEvent", in payload, OrderDescriptor.Instance);
        }

        [Fact]
        public void Write_Array_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_Array");
            var payload = new ArrayPayload { Values = [1, 2, 3, 4, 5] };
            es.Write("ArrayEvent", in payload, ArrayPayloadDescriptor.Instance);
        }

        [Fact]
        public void Write_NullString_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_NullString");
            var payload = new SimplePayload { Id = 0, Name = null };
            es.Write("NullStringEvent", in payload, SimplePayloadDescriptor.Instance);
        }

        [Fact]
        public void Write_NullArray_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_NullArray");
            var payload = new ArrayPayload { Values = null };
            es.Write("NullArrayEvent", in payload, ArrayPayloadDescriptor.Instance);
        }

        [Fact]
        public void Write_DisabledEventSource_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_Disabled");
            es.Dispose();
            var payload = new SimplePayload { Id = 1, Name = "test" };
            es.Write("AfterDispose", in payload, SimplePayloadDescriptor.Instance);
        }

        [Fact]
        public void Write_MetadataCaching_SameInstance()
        {
            var d1 = SimplePayloadDescriptor.Instance;
            var d2 = SimplePayloadDescriptor.Instance;
            Assert.Same(d1, d2);
            Assert.Same(d1.Metadata, d2.Metadata);
        }

        [Fact]
        public void TraceLoggingFieldDescriptor_ScalarProperties()
        {
            var field = new TraceLoggingFieldDescriptor("TestField", TraceLoggingDataType.Int32);
            Assert.Equal("TestField", field.Name);
            Assert.Equal(TraceLoggingDataType.Int32, field.DataType);
            Assert.False(field.IsArray);
            Assert.Empty(field.Children);
        }

        [Fact]
        public void TraceLoggingFieldDescriptor_GroupProperties()
        {
            var child1 = new TraceLoggingFieldDescriptor("A", TraceLoggingDataType.Int32);
            var child2 = new TraceLoggingFieldDescriptor("B", TraceLoggingDataType.UInt64);
            var group = new TraceLoggingFieldDescriptor("Group", child1, child2);
            Assert.Equal("Group", group.Name);
            Assert.Equal(TraceLoggingDataType.Struct, group.DataType);
            Assert.False(group.IsArray);
            Assert.Equal(2, group.Children.Length);
        }

        [Fact]
        public void TraceLoggingFieldDescriptor_ArrayProperties()
        {
            var field = new TraceLoggingFieldDescriptor("Arr", TraceLoggingDataType.Int32, isArray: true);
            Assert.Equal("Arr", field.Name);
            Assert.Equal(TraceLoggingDataType.Int32, field.DataType);
            Assert.True(field.IsArray);
            Assert.Empty(field.Children);
        }

        [Fact]
        public void TraceLoggingFieldDescriptor_NullName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new TraceLoggingFieldDescriptor(null!, TraceLoggingDataType.Int32));
        }

        [Fact]
        public void TraceLoggingEventMetadata_NullName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new TraceLoggingEventMetadata(null!));
        }

        [Fact]
        public void Write_MultipleEventsWithSameDescriptor_DoesNotThrow()
        {
            using var es = new EventSource("TestDescriptor_MultiWrite");
            for (int i = 0; i < 10; i++)
            {
                var payload = new SimplePayload { Id = i, Name = $"Event{i}" };
                es.Write("RepeatedEvent", in payload, SimplePayloadDescriptor.Instance);
            }
        }
    }
}
