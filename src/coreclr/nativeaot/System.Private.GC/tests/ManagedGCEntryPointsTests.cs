// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class ManagedGCEntryPointsTests : IDisposable
{
    private const int S_OK = 0;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const string TableResourceName = "GCInterfaceOffsets.h";

    private static readonly KeyValuePair<FieldInfo, object>[] s_declaredConfigValues = CaptureDeclaredConfigValues();
    private static readonly Dictionary<string, int> s_interfaceConstants = ReadInterfaceConstants();

    public ManagedGCEntryPointsTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    [Fact]
    public void VersionInfoReportsAbiAndCapturesRuntimeVersion()
    {
        byte* runtimeName = stackalloc byte[] { (byte)'E', (byte)'E', 0 };
        VersionInfo info = default;
        info.MajorVersion = 4;
        info.MinorVersion = 3;
        info.BuildVersion = 2;
        info.Name = runtimeName;

        ManagedGCEntryPoints.ManagedGC_VersionInfo(&info);

        VersionInfo runtimeSupportedVersion = ManagedGCEntryPoints.RuntimeSupportedVersion;
        Assert.Equal(4u, runtimeSupportedVersion.MajorVersion);
        Assert.Equal(3u, runtimeSupportedVersion.MinorVersion);
        Assert.Equal(2u, runtimeSupportedVersion.BuildVersion);
        Assert.Equal((nint)runtimeName, (nint)runtimeSupportedVersion.Name);

        Assert.Equal((uint)s_interfaceConstants["GC_INTERFACE_MAJOR_VERSION"], info.MajorVersion);
        Assert.Equal((uint)s_interfaceConstants["GC_INTERFACE_MINOR_VERSION"], info.MinorVersion);
        Assert.Equal(0u, info.BuildVersion);
        Assert.Equal("CoreCLR GC", ReadNullTerminatedUtf8(info.Name));
    }

    [Fact]
    public void InitializeFailsWhenClrToGcIsNull()
    {
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(null, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_FAIL, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(0, GCToEEInterface.InitializeCallCount);
        Assert.Equal(0, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(0, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(0, ManagedGCHeap.CreateCallCount);
    }

    [Fact]
    public void InitializeFailsWhenLayoutVerificationFails()
    {
        void* clrToGC = (void*)0x1234;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        GCInterfaceLayout.VerifyResult = false;

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_FAIL, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(0, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(0, ManagedGCHeap.CreateCallCount);
    }

    [Fact]
    public void InitializeReturnsOutOfMemoryWhenHandleManagerCreationFails()
    {
        void* clrToGC = (void*)0x1234;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        gcDacVars.major_version_number = 5;
        gcDacVars.minor_version_number = 8;
        ManagedGCHandleManager.SetCreateResult(null);

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_OUTOFMEMORY, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(1, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(0, ManagedGCHeap.CreateCallCount);
        Assert.Equal(5, gcDacVars.major_version_number);
        Assert.Equal(8, gcDacVars.minor_version_number);
    }

    [Fact]
    public void InitializeReturnsOutOfMemoryWhenHeapCreationFails()
    {
        void* clrToGC = (void*)0x1234;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        gcDacVars.major_version_number = 5;
        gcDacVars.minor_version_number = 8;
        ManagedGCHeap.SetCreateResult(null);

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_OUTOFMEMORY, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(1, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(1, ManagedGCHeap.CreateCallCount);
        Assert.Equal(5, gcDacVars.major_version_number);
        Assert.Equal(8, gcDacVars.minor_version_number);
    }

    [Fact]
    public void InitializeSucceedsAndReturnsHeapAndHandleManager()
    {
        void* clrToGC = (void*)0x1234;
        void* expectedHeap = (void*)0x1111;
        void* expectedHandleManager = (void*)0x2222;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        gcDacVars.major_version_number = 5;
        gcDacVars.minor_version_number = 8;
        ManagedGCHeap.SetCreateResult(expectedHeap);
        ManagedGCHandleManager.SetCreateResult(expectedHandleManager);

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(S_OK, result);
        Assert.Equal((nint)expectedHeap, (nint)gcHeap);
        Assert.Equal((nint)expectedHandleManager, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(1, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(1, ManagedGCHeap.CreateCallCount);
        Assert.Equal(5, gcDacVars.major_version_number);
        Assert.Equal(8, gcDacVars.minor_version_number);
#if USE_REGIONS
        Assert.Equal(GCSpinLock.lock_free, GCWriteBarrier.write_barrier_spin_lock.@lock);
        Assert.Equal(nuint.MaxValue, (nuint)gc_heap.ephemeral_low);
        Assert.Equal((nuint)0, (nuint)gc_heap.ephemeral_high);
#if DEBUG
        Assert.Equal(nuint.MaxValue, (nuint)GCWriteBarrier.write_barrier_spin_lock.holding_thread);
#endif
#endif
    }

    private static void ResetState()
    {
        RestoreDeclaredConfigValues();
        GCToEEInterface.Reset();
        GCInterfaceLayout.Reset();
        ManagedGCHeap.Reset();
        ManagedGCHandleManager.Reset();
    }

    private static string ReadNullTerminatedUtf8(byte* value)
    {
        int length = 0;
        while (value[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(value, length);
    }

    private static KeyValuePair<FieldInfo, object>[] CaptureDeclaredConfigValues() =>
        typeof(GCConfig)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(byte) || field.FieldType == typeof(long))
            .Select(field => new KeyValuePair<FieldInfo, object>(field, field.GetValue(null)))
            .ToArray();

    private static void RestoreDeclaredConfigValues()
    {
        foreach (KeyValuePair<FieldInfo, object> declared in s_declaredConfigValues)
        {
            declared.Key.SetValue(null, declared.Value);
        }
    }

    private static Dictionary<string, int> ReadInterfaceConstants()
    {
        using Stream stream = typeof(ManagedGCEntryPointsTests).Assembly.GetManifestResourceStream(TableResourceName);
        Assert.NotNull(stream);
        using StreamReader reader = new(stream);

        int column = IntPtr.Size == 8 ? 1 : 0;
        Dictionary<string, int> constants = new(StringComparer.Ordinal);
        while (reader.ReadLine() is string line)
        {
            if (!line.StartsWith("GC_CONST(", StringComparison.Ordinal))
            {
                continue;
            }

            int open = line.IndexOf('(');
            int close = line.LastIndexOf(')');
            Assert.True(open > 0 && close > open, $"Could not parse the table line '{line}'.");
            string[] arguments = line[(open + 1)..close].Split(',').Select(argument => argument.Trim()).ToArray();
            if (arguments.Length != 3)
            {
                continue;
            }

            constants[arguments[2]] = int.Parse(arguments[column], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return constants;
    }
}
