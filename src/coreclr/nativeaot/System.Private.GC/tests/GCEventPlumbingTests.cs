// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Direct tests for the ports of gcevent_serializers.h, gcevents.h, and the event-firing half of
// gceventstatus.h.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCEventPlumbingTests
{
    [Fact]
    public void SerializerWritesNativePrimitiveLayout()
    {
        byte* buffer = stackalloc byte[1 + 2 + 4 + 8 + 4];
        byte* cursor = buffer;

        GCEventSerializer.Serialize(ref cursor, (byte)0x12);
        GCEventSerializer.Serialize(ref cursor, (ushort)0x3456);
        GCEventSerializer.Serialize(ref cursor, 0x789ABCDEu);
        GCEventSerializer.Serialize(ref cursor, 0x0123456789ABCDEFul);
        GCEventSerializer.Serialize(ref cursor, 1.0f);

        Assert.Equal(19, cursor - buffer);
        Assert.Equal(
            new byte[]
            {
                0x12,
                0x56, 0x34,
                0xDE, 0xBC, 0x9A, 0x78,
                0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01,
                0x00, 0x00, 0x80, 0x3F,
            },
            new ReadOnlySpan<byte>(buffer, 19).ToArray());
    }

    [Fact]
    public void SerializerReportsNativePrimitiveSizes()
    {
        Assert.Equal((nuint)1, GCEventSerializer.SerializedSize((byte)0));
        Assert.Equal((nuint)2, GCEventSerializer.SerializedSize((ushort)0));
        Assert.Equal((nuint)4, GCEventSerializer.SerializedSize(0u));
        Assert.Equal((nuint)8, GCEventSerializer.SerializedSize(0ul));
        Assert.Equal((nuint)4, GCEventSerializer.SerializedSize(0.0f));
    }

    [Fact]
    public void ExpandedMethodsMatchEveryNativeEventRow()
    {
        string source = ReadEmbedded("gcevents.h");
        string[] knownEvents = Regex.Matches(source, @"^KNOWN_EVENT\(([^,]+),", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Match[] dynamicEvents = Regex.Matches(source, @"^DYNAMIC_EVENT\(([^,]+),[^,]+,[^,]+,\s*(\d+)", RegexOptions.Multiline)
            .Cast<Match>()
            .ToArray();

        Type type = typeof(GCEvents);
        Assert.Equal(37, knownEvents.Length);
        Assert.Equal(4, dynamicEvents.Length);

        foreach (string eventName in knownEvents)
        {
            Assert.NotNull(type.GetMethod($"GCEventEnabled{eventName}", BindingFlags.Public | BindingFlags.Static));
            Assert.NotNull(type.GetMethod($"GCEventFire{eventName}", BindingFlags.Public | BindingFlags.Static));
        }

        foreach (Match dynamicEvent in dynamicEvents)
        {
            string suffix = $"{dynamicEvent.Groups[1].Value}_V{dynamicEvent.Groups[2].Value}";
            Assert.NotNull(type.GetMethod($"GCEventEnabled{suffix}", BindingFlags.Public | BindingFlags.Static));
            Assert.NotNull(type.GetMethod($"GCEventFire{suffix}", BindingFlags.Public | BindingFlags.Static));
        }
    }

    [Fact]
    public void KnownEventFiresOnlyWhenItsProviderStateEnablesIt()
    {
        GCToEEInterface.Reset();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.Verbose);

        GCEvents.GCEventFireGCStart_V2(1, 2, 3, 4);
        Assert.Equal(GCToEEInterface.FiredEvent.None, GCToEEInterface.LastFiredEvent);

        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Warning);
        GCEvents.GCEventFireGCStart_V2(1, 2, 3, 4);
        Assert.Equal(GCToEEInterface.FiredEvent.None, GCToEEInterface.LastFiredEvent);

        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        GCEvents.GCEventFireGCStart_V2(1, 2, 3, 4);
        Assert.Equal(GCToEEInterface.FiredEvent.GCStart_V2, GCToEEInterface.LastFiredEvent);
    }

    [Fact]
    public void PrivateAndDefaultProvidersAreIndependent()
    {
        GCToEEInterface.Reset();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Verbose);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.Verbose);

        GCEvents.GCEventFireBGCBegin();
        Assert.Equal(GCToEEInterface.FiredEvent.None, GCToEEInterface.LastFiredEvent);

        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        GCEvents.GCEventFireBGCBegin();
        Assert.Equal(GCToEEInterface.FiredEvent.BGCBegin, GCToEEInterface.LastFiredEvent);
    }

    [Fact]
    public void DynamicEventsSerializeVersionAndCallSiteArguments()
    {
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);

        GCToEEInterface.Reset();
        GCEvents.GCEventFireCommittedUsage_V1(2, 3, 4, 5, 6);
        ReadOnlySpan<byte> payload = AssertDynamicEvent("CommittedUsage", 42);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(payload));
        Assert.Equal(2ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[2..]));
        Assert.Equal(3ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[10..]));
        Assert.Equal(4ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[18..]));
        Assert.Equal(5ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[26..]));
        Assert.Equal(6ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[34..]));

        GCToEEInterface.Reset();
        GCEvents.GCEventFireSizeAdaptationTuning_V1(2, 3, 4, 5, 6, 7.0f, 8.0f, 9.0f, 10, 11.0f, 12, 13, 14, 15, 16, 17, 18);
        payload = AssertDynamicEvent("SizeAdaptationTuning", 56);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(payload));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]));
        Assert.Equal(5ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]));
        Assert.Equal(6ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]));
        Assert.Equal(7.0f, ReadSingle(payload[24..]));
        Assert.Equal(8.0f, ReadSingle(payload[28..]));
        Assert.Equal(9.0f, ReadSingle(payload[32..]));
        Assert.Equal((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(payload[36..]));
        Assert.Equal(11.0f, ReadSingle(payload[38..]));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(payload[42..]));
        Assert.Equal((byte)13, payload[46]);
        Assert.Equal((ushort)14, BinaryPrimitives.ReadUInt16LittleEndian(payload[47..]));
        Assert.Equal((ushort)15, BinaryPrimitives.ReadUInt16LittleEndian(payload[49..]));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(payload[51..]));
        Assert.Equal((ushort)17, BinaryPrimitives.ReadUInt16LittleEndian(payload[53..]));
        Assert.Equal((byte)18, payload[55]);

        GCToEEInterface.Reset();
        GCEvents.GCEventFireSizeAdaptationFullGCTuning_V1(2, 3, 4.0f, 5, 6, 7.0f, 8, 9.0f, 10, 11.0f);
        payload = AssertDynamicEvent("SizeAdaptationFullGCTuning", 44);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(payload));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]));
        Assert.Equal(3ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[4..]));
        Assert.Equal(4.0f, ReadSingle(payload[12..]));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]));
        Assert.Equal(7.0f, ReadSingle(payload[24..]));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(payload[28..]));
        Assert.Equal(9.0f, ReadSingle(payload[32..]));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(payload[36..]));
        Assert.Equal(11.0f, ReadSingle(payload[40..]));

        GCToEEInterface.Reset();
        GCEvents.GCEventFireSizeAdaptationSample_V1(2, 3, 4, 5, 6, 7, 8);
        payload = AssertDynamicEvent("SizeAdaptationSample", 38);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(payload));
        Assert.Equal(2ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[2..]));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(payload[14..]));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(payload[18..]));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(payload[22..]));
        Assert.Equal(7ul, BinaryPrimitives.ReadUInt64LittleEndian(payload[26..]));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(payload[34..]));
    }

    private static ReadOnlySpan<byte> AssertDynamicEvent(string name, int payloadSize)
    {
        Assert.Equal(GCToEEInterface.FiredEvent.Dynamic, GCToEEInterface.LastFiredEvent);
        Assert.Equal(name, GCToEEInterface.LastDynamicEventName);
        Assert.Equal(payloadSize, GCToEEInterface.LastDynamicEventPayload.Length);

        return GCToEEInterface.LastDynamicEventPayload;
    }

    private static float ReadSingle(ReadOnlySpan<byte> payload) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload));

    private static string ReadEmbedded(string suffix)
    {
        Assembly assembly = typeof(GCEventPlumbingTests).Assembly;
        string resourceName = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
