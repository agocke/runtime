// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitutes for the pieces ManagedGCEntryPoints depends on, so its gcload.cpp
// translation can be tested directly without pulling in the full heap implementation.
//
// GCInterfaceOffsets is also the stand-in for generated constants used by directly compiled
// collector leaves: the test project does not compile the real generated table (see
// src/System.Private.GC.csproj's InPlaceRuntime item group), so this hand-maintained subset is the
// same substitution point GC_INTERFACE_MAJOR_VERSION/MINOR_VERSION already use.

namespace Internal.Runtime.GarbageCollection;

internal static class GCInterfaceOffsets
{
    public const int GC_INTERFACE_MAJOR_VERSION = 5;
    public const int GC_INTERFACE_MINOR_VERSION = 8;
    public const int max_generation = 2;
    public const int MAX_BUCKET_COUNT = 20;
#if TARGET_64BIT
    public const int min_obj_size = 0x18;
#else
    public const int min_obj_size = 0x0c;
#endif

    // SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift of gcinterface.h, read directly by
    // SoftwareWriteWatch rather than being restated as a private constant of its own.
    public const int SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift = 0xc;

    public const int OFFSETOF__HandleTable__rgTypeFlags = 0;
#if TARGET_64BIT
    public const int OFFSETOF__HandleTable__pSegmentList = 0x38;
    public const int OFFSETOF__HandleTable__Lock = 0x40;
#if DEBUG
    public const int OFFSETOF__HandleTable__uTypeCount = 0x78;
    public const int OFFSETOF__HandleTable__dwCount = 0x7c;
    public const int OFFSETOF__HandleTable__pAsyncScanInfo = 0x80;
    public const int OFFSETOF__HandleTable__uTableIndex = 0x88;
    public const int OFFSETOF__HandleTable__rgQuickCache = 0x90;
    public const int OFFSETOF__HandleTable___DEBUG_iMaxGen = 0xf8;
    public const int OFFSETOF__HandleTable___DEBUG_TotalBlocksScanned = 0x100;
    public const int OFFSETOF__HandleTable___DEBUG_TotalBlocksScannedNonTrivially = 0x128;
    public const int OFFSETOF__HandleTable___DEBUG_TotalHandleSlotsScanned = 0x150;
    public const int OFFSETOF__HandleTable___DEBUG_TotalHandlesActuallyScanned = 0x178;
    public const int SIZEOF__HandleTable = 0x1a0;
#else
    public const int OFFSETOF__HandleTable__uTypeCount = 0x68;
    public const int OFFSETOF__HandleTable__dwCount = 0x6c;
    public const int OFFSETOF__HandleTable__pAsyncScanInfo = 0x70;
    public const int OFFSETOF__HandleTable__uTableIndex = 0x78;
    public const int OFFSETOF__HandleTable__rgQuickCache = 0x80;
    public const int SIZEOF__HandleTable = 0xe8;
#endif
#else
    public const int OFFSETOF__HandleTable__pSegmentList = 0x34;
    public const int OFFSETOF__HandleTable__Lock = 0x38;
#if DEBUG
    public const int OFFSETOF__HandleTable__uTypeCount = 0x58;
    public const int OFFSETOF__HandleTable__dwCount = 0x5c;
    public const int OFFSETOF__HandleTable__pAsyncScanInfo = 0x60;
    public const int OFFSETOF__HandleTable__uTableIndex = 0x64;
    public const int OFFSETOF__HandleTable__rgQuickCache = 0x68;
    public const int OFFSETOF__HandleTable___DEBUG_iMaxGen = 0x9c;
    public const int OFFSETOF__HandleTable___DEBUG_TotalBlocksScanned = 0xa0;
    public const int OFFSETOF__HandleTable___DEBUG_TotalBlocksScannedNonTrivially = 0xc8;
    public const int OFFSETOF__HandleTable___DEBUG_TotalHandleSlotsScanned = 0xf0;
    public const int OFFSETOF__HandleTable___DEBUG_TotalHandlesActuallyScanned = 0x118;
    public const int SIZEOF__HandleTable = 0x140;
#else
    public const int OFFSETOF__HandleTable__uTypeCount = 0x50;
    public const int OFFSETOF__HandleTable__dwCount = 0x54;
    public const int OFFSETOF__HandleTable__pAsyncScanInfo = 0x58;
    public const int OFFSETOF__HandleTable__uTableIndex = 0x5c;
    public const int OFFSETOF__HandleTable__rgQuickCache = 0x60;
    public const int SIZEOF__HandleTable = 0x94;
#endif
#endif
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
    internal const uint MaxGeneration = 2;

    private static nint s_createResult = 1;
    private static int s_gcStarted;

    public static int CreateCallCount { get; private set; }

    public static uint TestGeneration { get; set; }

    public static nint TestGenerationObject { get; set; }

    public static uint TestGenerationForObject { get; set; }

    public static bool ConcurrentCollectionInProgress { get; set; }

    public static nuint TestPromotedBytes { get; set; }

    public static void* Create()
    {
        CreateCallCount++;
        return (void*)s_createResult;
    }

    internal static uint GenerationOf(byte* obj) =>
        (nint)obj == TestGenerationObject
            ? TestGenerationForObject
            : TestGeneration;

    internal static bool IsPromoted(byte* obj) =>
        obj is null || ((CObjectHeader*)obj)->IsMarked() != 0;

    internal static bool IsPromotedForBridge(byte* obj) =>
        IsPromoted(obj);

    internal static void DiagWalkObjectForBridge(
        byte* obj,
        delegate*<byte*, void*, byte> callback,
        void* context)
    {
        _ = obj;
        _ = callback;
        _ = context;
    }

    internal static nuint GetPromotedBytesForHandleScan(int heap_index)
    {
        _ = heap_index;
        return TestPromotedBytes;
    }

    internal static bool CollectionStartedForAllocation() => s_gcStarted != 0;

    internal static void NotifyCollectionStarted() => s_gcStarted++;

    internal static void NotifyCollectionEnded() => s_gcStarted--;

    internal static void RecordCollectionCount(int collectionCount)
    {
        _ = collectionCount;
    }

    public static void SetCreateResult(void* createResult) => s_createResult = (nint)createResult;

    public static void Reset()
    {
        s_createResult = 1;
        CreateCallCount = 0;
        TestGeneration = 0;
        TestGenerationObject = 0;
        TestGenerationForObject = 0;
        ConcurrentCollectionInProgress = false;
        TestPromotedBytes = 0;
        s_gcStarted = 0;
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
