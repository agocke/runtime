// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitutes for the pieces ManagedGCEntryPoints depends on, so its gcload.cpp
// translation can be tested directly without pulling in the full heap implementation.
//
// GCInterfaceOffsets is also the stand-in SoftwareWriteWatch reads its
// SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift constant from: the test project does not
// compile the real generated table (see src/System.Private.GC.csproj's InPlaceRuntime item
// group), so this hand-maintained subset of it is the same substitution point
// GC_INTERFACE_MAJOR_VERSION/MINOR_VERSION already use.

namespace Internal.Runtime.GarbageCollection;

internal static class GCInterfaceOffsets
{
    public const int GC_INTERFACE_MAJOR_VERSION = 5;
    public const int GC_INTERFACE_MINOR_VERSION = 8;

    // SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift of gcinterface.h, read directly by
    // SoftwareWriteWatch rather than being restated as a private constant of its own.
    public const int SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift = 0xc;
}

internal static class GCInterfaceLayout
{
    public static bool VerifyResult { get; set; } = true;

    public static int VerifyCallCount { get; private set; }

    public static bool Verify()
    {
        VerifyCallCount++;
        return VerifyResult;
    }

    public static void Reset()
    {
        VerifyResult = true;
        VerifyCallCount = 0;
    }
}

internal static unsafe class ManagedGCHeap
{
    private static nint s_createResult = 1;

    public static int CreateCallCount { get; private set; }

    public static void* Create()
    {
        CreateCallCount++;
        return (void*)s_createResult;
    }

    public static void SetCreateResult(void* createResult) => s_createResult = (nint)createResult;

    public static void Reset()
    {
        s_createResult = 1;
        CreateCallCount = 0;
    }
}

internal static unsafe class ManagedGCHandleManager
{
    private static nint s_createResult = 1;

    public static int CreateCallCount { get; private set; }

    public static void* Create()
    {
        CreateCallCount++;
        return (void*)s_createResult;
    }

    public static void SetCreateResult(void* createResult) => s_createResult = (nint)createResult;

    public static void Reset()
    {
        s_createResult = 1;
        CreateCallCount = 0;
    }
}
