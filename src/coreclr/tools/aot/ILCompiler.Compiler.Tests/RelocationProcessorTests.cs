// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using ILCompiler.Linker;
using Xunit;
using static ILCompiler.ObjectWriter.ElfNative;

namespace ILCompiler.Compiler.Tests
{
    public class RelocationProcessorTests
    {
        [Fact]
        public void AppliesR_X86_64_64()
        {
            byte[] buffer = new byte[8];
            ulong symbolAddr = 0x401000;
            long addend = 0x10;

            RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_64,
                symbolAddr, relocAddress: 0, addend);

            ulong result = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            Assert.Equal(symbolAddr + (ulong)addend, result);
        }

        [Fact]
        public void AppliesR_X86_64_PC32()
        {
            byte[] buffer = new byte[4];
            ulong symbolAddr = 0x401100;
            ulong relocAddr = 0x401000;
            long addend = -4;

            RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_PC32,
                symbolAddr, relocAddr, addend);

            int result = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            // S + A - P = 0x401100 + (-4) - 0x401000 = 0xFC
            Assert.Equal(0xFC, result);
        }

        [Fact]
        public void AppliesR_X86_64_PLT32()
        {
            byte[] buffer = new byte[4];
            ulong symbolAddr = 0x402000;
            ulong relocAddr = 0x401000;
            long addend = -4;

            RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_PLT32,
                symbolAddr, relocAddr, addend);

            int result = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            // S + A - P = 0x402000 + (-4) - 0x401000 = 0xFFC
            Assert.Equal(0xFFC, result);
        }

        [Fact]
        public void AppliesR_X86_64_32()
        {
            byte[] buffer = new byte[4];
            ulong symbolAddr = 0x1000;
            long addend = 0x20;

            RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_32,
                symbolAddr, relocAddress: 0, addend);

            uint result = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            Assert.Equal(0x1020U, result);
        }

        [Fact]
        public void AppliesR_X86_64_32S()
        {
            byte[] buffer = new byte[4];
            ulong symbolAddr = 0x1000;
            long addend = -0x10;

            RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_32S,
                symbolAddr, relocAddress: 0, addend);

            int result = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            Assert.Equal(0xFF0, result);
        }

        [Fact]
        public void R_X86_64_NONE_DoesNothing()
        {
            byte[] buffer = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            byte[] expected = (byte[])buffer.Clone();

            RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_NONE,
                symbolAddress: 0, relocAddress: 0, addend: 0);

            Assert.Equal(expected, buffer);
        }

        [Fact]
        public void PC32_OverflowThrows()
        {
            byte[] buffer = new byte[4];
            // Create values that will overflow a 32-bit signed range
            ulong symbolAddr = 0x8000000000;
            ulong relocAddr = 0x0;
            long addend = 0;

            Assert.Throws<LinkerException>(() =>
                RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_PC32,
                    symbolAddr, relocAddr, addend));
        }

        [Fact]
        public void R_X86_64_32_OverflowThrows()
        {
            byte[] buffer = new byte[4];
            ulong symbolAddr = 0x1_0000_0000; // exceeds uint.MaxValue
            long addend = 0;

            Assert.Throws<LinkerException>(() =>
                RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_32,
                    symbolAddr, relocAddress: 0, addend));
        }

        [Fact]
        public void R_X86_64_32S_OverflowThrows()
        {
            byte[] buffer = new byte[4];
            ulong symbolAddr = 0x8000_0000; // int.MaxValue + 1
            long addend = 1;

            Assert.Throws<LinkerException>(() =>
                RelocationProcessor.ApplyRelocation(buffer, 0, R_X86_64_32S,
                    symbolAddr, relocAddress: 0, addend));
        }

        [Fact]
        public void UnsupportedRelocTypeThrows()
        {
            byte[] buffer = new byte[8];

            Assert.Throws<LinkerException>(() =>
                RelocationProcessor.ApplyRelocation(buffer, 0, 9999,
                    symbolAddress: 0, relocAddress: 0, addend: 0));
        }

        [Fact]
        public void AppliesAtCorrectOffset()
        {
            byte[] buffer = new byte[16];
            buffer.AsSpan().Fill(0xFF);

            ulong symbolAddr = 0x42;
            RelocationProcessor.ApplyRelocation(buffer, 8, R_X86_64_64,
                symbolAddr, relocAddress: 0, addend: 0);

            // Bytes 0-7 should be untouched
            for (int i = 0; i < 8; i++)
                Assert.Equal(0xFF, buffer[i]);

            ulong result = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
            Assert.Equal(0x42UL, result);
        }
    }
}
