// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/gcevent_serializers.h.

using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>Serialization traits and plumbing for serializing dynamic GC events.</summary>
    internal static unsafe class GCEventSerializer
    {
        public static nuint SerializedSize(byte _) => sizeof(byte);

        public static nuint SerializedSize(ushort _) => sizeof(ushort);

        public static nuint SerializedSize(uint _) => sizeof(uint);

        public static nuint SerializedSize(ulong _) => sizeof(ulong);

        public static nuint SerializedSize(float _) => sizeof(float);

        public static void Serialize(ref byte* buffer, byte value)
        {
            *buffer = value;
            buffer += sizeof(byte);
        }

        public static void Serialize(ref byte* buffer, ushort value)
        {
            Unsafe.WriteUnaligned(buffer, value);
            buffer += sizeof(ushort);
        }

        public static void Serialize(ref byte* buffer, uint value)
        {
            Unsafe.WriteUnaligned(buffer, value);
            buffer += sizeof(uint);
        }

        public static void Serialize(ref byte* buffer, ulong value)
        {
            Unsafe.WriteUnaligned(buffer, value);
            buffer += sizeof(ulong);
        }

        public static void Serialize(ref byte* buffer, float value)
        {
            Unsafe.WriteUnaligned(buffer, value);
            buffer += sizeof(float);
        }
    }
}
