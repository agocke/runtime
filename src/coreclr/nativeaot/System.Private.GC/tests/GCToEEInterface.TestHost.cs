// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Interface/GCToEEInterface.cs.
//
// GCConfig is the one ported file whose "underneath" is the EE rather than libc: every value it
// caches comes from GCToEEInterface, which in the shipping build is an indirect call through the
// IGCToCLR vtable the EE handed the GC at load time. There is no such instance in a test process,
// and a managed callback cannot stand in for one either -- the vtable slots are
// delegate* unmanaged[SuppressGCTransition], and CoreCLR rejects a call to an
// [UnmanagedCallersOnly] method through one -- so the substitution point is this class, exactly
// as the libc and Win32 imports are the substitution point for the platform ports.
//
// It models what nativeaot/Runtime/gcenv.ee.cpp does, because that is the behavior GCConfig is
// written against:
//
//   * the private key is looked up first, in the DOTNET_ environment settings;
//   * the public key is looked up only when the config has one, in the runtimeconfig knobs;
//   * a boolean is whatever the EE read compared against zero, so the GC only ever sees 0 or 1;
//   * an integer is the uint64 the EE read, reinterpreted as int64;
//   * a string is allocated by the EE and must be given back to FreeStringConfigValue.
//
// Every call is recorded, so the tests can assert the key sequence GCConfig asks for rather than
// only the values it ends up with.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Internal.Runtime.GarbageCollection;

/// <summary>One configuration request, as <c>GCConfig</c> made it.</summary>
internal sealed class ConfigRequest
{
    public ConfigRequest(string kind, string privateKey, string publicKey)
    {
        Kind = kind;
        PrivateKey = privateKey;
        PublicKey = publicKey;
    }

    /// <summary>"bool", "int" or "string": which of the three EE getters was called.</summary>
    public string Kind { get; }

    /// <summary>The private key the GC passed. Never null: every config has one.</summary>
    public string PrivateKey { get; }

    /// <summary>The public key the GC passed, or null where it passed a null pointer.</summary>
    public string PublicKey { get; }

    public override string ToString() => $"{Kind}({PrivateKey}, {PublicKey ?? "null"})";
}

internal static unsafe class GCToEEInterface
{
    internal static void* LastInitializedGCToCLR { get; private set; }

    internal static int InitializeCallCount { get; private set; }

    /// <summary>Values reachable through the private key, i.e. the DOTNET_ settings.</summary>
    private static readonly Dictionary<string, ulong> s_privateValues = new(StringComparer.Ordinal);

    /// <summary>Values reachable through the public key, i.e. the runtimeconfig knobs.</summary>
    private static readonly Dictionary<string, ulong> s_publicValues = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> s_privateStrings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> s_publicStrings = new(StringComparer.Ordinal);

    /// <summary>Every request, in the order the GC made it.</summary>
    internal static List<ConfigRequest> Requests { get; } = new();

    /// <summary>Strings handed to the GC that have not been given back yet.</summary>
    internal static List<IntPtr> OutstandingStrings { get; } = new();

    /// <summary>Every pointer passed to <see cref="FreeStringConfigValue"/>, including null.</summary>
    internal static List<IntPtr> FreedStrings { get; } = new();

    /// <summary>How many times the port called <see cref="StompWriteBarrier"/>.</summary>
    internal static int StompWriteBarrierCallCount { get; private set; }

    /// <summary>
    /// The <see cref="WriteBarrierParameters"/> the most recent <see cref="StompWriteBarrier"/>
    /// call passed, copied out of the pointer -- which the caller may free or overwrite on
    /// return -- so tests can inspect it afterwards.
    /// </summary>
    internal static WriteBarrierParameters LastStompWriteBarrier { get; private set; }

    internal static void Reset()
    {
        LastInitializedGCToCLR = null;
        InitializeCallCount = 0;
        s_privateValues.Clear();
        s_publicValues.Clear();
        s_privateStrings.Clear();
        s_publicStrings.Clear();
        Requests.Clear();
        FreedStrings.Clear();
        WriteWithoutProviding = null;
        StompWriteBarrierCallCount = 0;
        LastStompWriteBarrier = default;

        foreach (IntPtr outstanding in OutstandingStrings)
        {
            NativeMemory.Free((void*)outstanding);
        }

        OutstandingStrings.Clear();
    }

    public static void Initialize(void* theGCToCLR)
    {
        LastInitializedGCToCLR = theGCToCLR;
        InitializeCallCount++;
    }

    /// <summary>
    /// Substitute for the indirect <c>IGCToCLR::StompWriteBarrier</c> call.
    /// <see cref="SoftwareWriteWatch.EnableForGCHeap"/> and
    /// <see cref="SoftwareWriteWatch.DisableForGCHeap"/> are the only callers this file's tests
    /// exercise, so recording the arguments is enough; no real write barrier is bashed.
    /// </summary>
    public static void StompWriteBarrier(WriteBarrierParameters* args)
    {
        StompWriteBarrierCallCount++;
        LastStompWriteBarrier = *args;
    }

    internal static void SetPrivateValue(string privateKey, ulong value) => s_privateValues[privateKey] = value;

    internal static void SetPublicValue(string publicKey, ulong value) => s_publicValues[publicKey] = value;

    internal static void SetPrivateString(string privateKey, string value) => s_privateStrings[privateKey] = value;

    internal static void SetPublicString(string publicKey, string value) => s_publicStrings[publicKey] = value;

    /// <summary>
    /// When non-null, the boolean and integer getters write this value through the out pointer
    /// and then report that the config was not provided, which is what a misbehaving EE does and
    /// what the C++ leaves visible by passing the address of the cached value straight down.
    /// </summary>
    internal static ulong? WriteWithoutProviding { get; set; }

    public static byte GetBooleanConfigValue(byte* privateKey, byte* publicKey, byte* value)
    {
        string privateName = ToManagedString(privateKey);
        string publicName = ToManagedString(publicKey);
        Requests.Add(new ConfigRequest("bool", privateName, publicName));

        if (WriteWithoutProviding is ulong written)
        {
            *value = (byte)(written != 0 ? 1 : 0);
            return 0;
        }

        if (s_privateValues.TryGetValue(privateName, out ulong privateValue))
        {
            *value = (byte)(privateValue != 0 ? 1 : 0);
            return 1;
        }

        if (publicName is not null && s_publicValues.TryGetValue(publicName, out ulong publicValue))
        {
            *value = (byte)(publicValue != 0 ? 1 : 0);
            return 1;
        }

        return 0;
    }

    public static byte GetIntConfigValue(byte* privateKey, byte* publicKey, long* value)
    {
        string privateName = ToManagedString(privateKey);
        string publicName = ToManagedString(publicKey);
        Requests.Add(new ConfigRequest("int", privateName, publicName));

        if (WriteWithoutProviding is ulong written)
        {
            *value = (long)written;
            return 0;
        }

        if (s_privateValues.TryGetValue(privateName, out ulong privateValue))
        {
            *value = (long)privateValue;
            return 1;
        }

        if (publicName is not null && s_publicValues.TryGetValue(publicName, out ulong publicValue))
        {
            *value = (long)publicValue;
            return 1;
        }

        return 0;
    }

    public static byte GetStringConfigValue(byte* privateKey, byte* publicKey, byte** value)
    {
        string privateName = ToManagedString(privateKey);
        string publicName = ToManagedString(publicKey);
        Requests.Add(new ConfigRequest("string", privateName, publicName));

        if (s_privateStrings.TryGetValue(privateName, out string privateValue))
        {
            *value = Allocate(privateValue);
            return 1;
        }

        if (publicName is not null && s_publicStrings.TryGetValue(publicName, out string publicValue))
        {
            *value = Allocate(publicValue);
            return 1;
        }

        return 0;
    }

    public static void FreeStringConfigValue(byte* value)
    {
        FreedStrings.Add((IntPtr)value);

        if (value is null)
        {
            return;
        }

        // A double free, or a free of something the EE never handed out, is a test failure that
        // has to be visible rather than a heap corruption.
        if (!OutstandingStrings.Remove((IntPtr)value))
        {
            throw new InvalidOperationException("Freed a config string that was not outstanding.");
        }

        NativeMemory.Free(value);
    }

    /// <summary>The UTF-8 bytes of a string the tests handed out, as the caller sees them.</summary>
    internal static string ReadString(byte* value) => ToManagedString(value);

    private static byte* Allocate(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Span<byte> destination = new Span<byte>(buffer, length + 1);
        Encoding.UTF8.GetBytes(value, destination);
        destination[length] = 0;

        OutstandingStrings.Add((IntPtr)buffer);
        return buffer;
    }

    private static string ToManagedString(byte* value)
    {
        if (value is null)
        {
            return null;
        }

        int length = 0;
        while (value[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(value, length);
    }
}
