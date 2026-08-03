// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Direct constants and layout tests for the first handletableconstants.h/handletablepriv.h
// translation slice.

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class HandleTableTests
{
    [Fact]
    public void ConstantsMatchTheNativeTargetLayout()
    {
        Assert.Equal(10, HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE);
        Assert.Equal(13, HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);
        Assert.Equal(12, HandleTableConstants.HANDLE_MAX_PUBLIC_TYPES);
        Assert.Equal(12, HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK);
        Assert.Equal(0u, HandleTableConstants.HNDF_NORMAL);
        Assert.Equal(1u, HandleTableConstants.HNDF_EXTRAINFO);

        Assert.Equal(65536, HandleTableConstants.HANDLE_SEGMENT_SIZE);
        Assert.Equal(4096, HandleTableConstants.HANDLE_HEADER_SIZE);
        Assert.Equal(65536, HandleTableConstants.HANDLE_SEGMENT_ALIGNMENT);
        Assert.Equal(HandleTableConstants.HANDLE_SIZE, IntPtr.Size);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT, (65536 - 4096) / IntPtr.Size);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT / 64, HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT / 16, HandleTableConstants.HANDLE_CLUMPS_PER_SEGMENT);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT / 32, HandleTableConstants.HANDLE_MASKS_PER_SEGMENT);
        Assert.Equal(4, HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK);
        Assert.Equal(HandleTableConstants.HANDLE_BYTES_PER_BLOCK, 64 * IntPtr.Size);
        Assert.Equal(32, HandleTableConstants.HANDLE_HANDLES_PER_MASK);
        Assert.Equal(2, HandleTableConstants.HANDLE_MASKS_PER_BLOCK);
        Assert.Equal(2, HandleTableConstants.HANDLE_CLUMPS_PER_MASK);
        Assert.Equal(
            HandleTableConstants.HANDLE_HANDLES_PER_BLOCK,
            HandleTableConstants.HANDLE_HANDLES_PER_MASK * 2);

        Assert.Equal(63, HandleTableConstants.HANDLES_PER_CACHE_BANK);
        Assert.Equal(21, HandleTableConstants.REBALANCE_TOLERANCE);
        Assert.Equal(42, HandleTableConstants.REBALANCE_LOWATER_MARK);
        Assert.Equal(84, HandleTableConstants.REBALANCE_HIWATER_MARK);
        Assert.Equal(6, HandleTableConstants.SMALL_ALLOC_COUNT);

        Assert.Equal(0x000000FFu, HandleTableConstants.GEN_CLUMP_0_MASK);
        Assert.Equal(0x00123456u, HandleTableConstants.NEXT_CLUMP_IN_MASK(0x12345678));
        Assert.Equal(0x0000FFFFul, (ulong)HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK);
        nuint expectedSegmentAlignMask = nuint.MaxValue;
        expectedSegmentAlignMask -= 0xFFFF;
        nuint actualSegmentAlignMask = unchecked((nuint)HandleTableConstants.HANDLE_SEGMENT_ALIGN_MASK);
        Assert.Equal(expectedSegmentAlignMask, actualSegmentAlignMask);
    }

    [Fact]
    public void HandleTableTypeFlagsStayAtTheNativePrefix()
    {
        AssertOffset<HandleTable>(nameof(HandleTable.rgTypeFlags), GCInterfaceOffsets.OFFSETOF__HandleTable__rgTypeFlags);
        AssertOffset<HandleTable>(nameof(HandleTable.pSegmentList), GCInterfaceOffsets.OFFSETOF__HandleTable__pSegmentList);
        AssertOffset<HandleTable>(nameof(HandleTable.Lock), GCInterfaceOffsets.OFFSETOF__HandleTable__Lock);
        AssertOffset<HandleTable>(nameof(HandleTable.uTypeCount), GCInterfaceOffsets.OFFSETOF__HandleTable__uTypeCount);
        AssertOffset<HandleTable>(nameof(HandleTable.dwCount), GCInterfaceOffsets.OFFSETOF__HandleTable__dwCount);
        AssertOffset<HandleTable>(nameof(HandleTable.pAsyncScanInfo), GCInterfaceOffsets.OFFSETOF__HandleTable__pAsyncScanInfo);
        AssertOffset<HandleTable>(nameof(HandleTable.uTableIndex), GCInterfaceOffsets.OFFSETOF__HandleTable__uTableIndex);
        AssertOffset<HandleTable>(nameof(HandleTable.rgQuickCache), GCInterfaceOffsets.OFFSETOF__HandleTable__rgQuickCache);
#if DEBUG
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_iMaxGen), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_iMaxGen);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalBlocksScanned), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalBlocksScanned);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalBlocksScannedNonTrivially), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalBlocksScannedNonTrivially);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalHandleSlotsScanned), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalHandleSlotsScanned);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalHandlesActuallyScanned), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalHandlesActuallyScanned);
#endif
        Assert.Equal(GCInterfaceOffsets.SIZEOF__HandleTable, sizeof(HandleTable));
    }

    [Fact]
    public void HandleTableLifecycleInitializesSegmentsFlagsAndCaches()
    {
        const uint TypeCount = 12;

        uint* typeFlags = stackalloc uint[(int)TypeCount];
        typeFlags[4] = HandleTableConstants.HNDF_EXTRAINFO;
        typeFlags[6] = HandleTableConstants.HNDF_EXTRAINFO;

        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, TypeCount);
        Assert.True(table != null);

        try
        {
            Assert.Equal(TypeCount, table->uTypeCount);
            Assert.Equal(uint.MaxValue, table->uTableIndex);
            Assert.Equal(HandleTableConstants.HNDF_EXTRAINFO, table->rgTypeFlags[4]);
            Assert.Equal(HandleTableConstants.HNDF_EXTRAINFO, table->rgTypeFlags[6]);
            Assert.Equal(HandleTableConstants.HNDF_NORMAL, table->rgTypeFlags[12]);
            Assert.True(table->pSegmentList != null);
            Assert.True(table->pSegmentList->Header.pHandleTable == table);

            HandleTypeCache* mainCache = HandleTableManager.GetMainCache(table);
            for (uint type = 0; type < TypeCount; type++)
            {
                Assert.Equal(HandleTableConstants.HANDLES_PER_CACHE_BANK, mainCache[type].lFreeIndex);
            }

            HandleTableManager.HndSetHandleTableIndex(table, 17);
            Assert.Equal(17u, HandleTableManager.HndGetHandleTableIndex(table));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void ObjectHandleInitializationCreatesNativeMapAndBucketShape()
    {
        AssertOffset<HandleTableMap>(nameof(HandleTableMap.pBuckets), 0);
        AssertOffset<HandleTableMap>(nameof(HandleTableMap.pNext), IntPtr.Size);
        AssertOffset<HandleTableMap>(nameof(HandleTableMap.dwMaxIndex), IntPtr.Size * 2);
        Assert.Equal(IntPtr.Size == 8 ? 24 : 12, sizeof(HandleTableMap));
        AssertOffset<HandleTableBucket>(nameof(HandleTableBucket.pTable), 0);
        AssertOffset<HandleTableBucket>(nameof(HandleTableBucket.HandleTableIndex), IntPtr.Size);
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8, sizeof(HandleTableBucket));

        Assert.Equal(0, (nint)ObjectHandle.g_HandleTableMap.pBuckets);
        Assert.True(ObjectHandle.Ref_Initialize());

        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);
        try
        {
            Assert.Equal(
                (uint)HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE,
                ObjectHandle.g_HandleTableMap.dwMaxIndex);
            Assert.Equal(0, (nint)ObjectHandle.g_HandleTableMap.pNext);
            Assert.True(ObjectHandle.g_HandleTableMap.pBuckets[0] == bucket);
            Assert.Equal(0u, bucket->HandleTableIndex);
            Assert.True(bucket->pTable != null);
            Assert.True(bucket->pTable[0] != null);
            Assert.Equal(0u, HandleTableManager.HndGetHandleTableIndex(bucket->pTable[0]));
            Assert.False(ObjectHandle.Contains(bucket, default));

            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(
                bucket->pTable[0],
                (uint)HandleType.HNDTYPE_DEPENDENT,
                (byte*)0x1234,
                0x5678);
            Assert.True(ObjectHandle.Contains(bucket, handle));
            Assert.Equal((nuint)0x5678, HandleTableManager.HndGetHandleExtraInfo(handle));
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }

        Assert.Equal(0, (nint)ObjectHandle.g_HandleTableMap.pBuckets);
        Assert.Equal(0, (nint)bucket->pTable);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ObjectHandleInitializationCleansUpAllocationFailures(int allocationToFail)
    {
        SyncImports.FailAllocOnCall = allocationToFail;

        try
        {
            Assert.False(ObjectHandle.Ref_Initialize());
            Assert.Equal(0, SyncImports.FailAllocOnCall);
            Assert.Equal(0, (nint)ObjectHandle.g_HandleTableMap.pBuckets);
            Assert.Equal(
                0,
                (nint)ObjectHandle.g_GlobalHandleTableBucket.pTable);
        }
        finally
        {
            SyncImports.FailAllocOnCall = 0;
        }
    }

    [Fact]
    public void CountAllHandlesWalksTheHandleTableMap()
    {
        Assert.True(ObjectHandle.Ref_Initialize());
        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);

        try
        {
            OBJECTHANDLE first = HandleTableManager.HndCreateHandle(
                bucket->pTable[0],
                (uint)HandleType.HNDTYPE_STRONG,
                (byte*)0x1234,
                0);
            OBJECTHANDLE second = HandleTableManager.HndCreateHandle(
                bucket->pTable[0],
                (uint)HandleType.HNDTYPE_PINNED,
                (byte*)0x5678,
                0);

            Assert.Equal(2u, HandleTableManager.HndCountAllHandles(fUseLocks: false));
            Assert.Equal(2u, HandleTableManager.HndCountAllHandles(fUseLocks: true));

            HandleTableManager.HndDestroyHandleOfUnknownType(bucket->pTable[0], first);
            HandleTableManager.HndDestroyHandleOfUnknownType(bucket->pTable[0], second);
            Assert.Equal(0u, HandleTableManager.HndCountAllHandles(fUseLocks: true));
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }
    }

    [Theory]
    [InlineData(ObjectHandle.VHT_WEAK_SHORT)]
    [InlineData(ObjectHandle.VHT_WEAK_LONG)]
    [InlineData(ObjectHandle.VHT_STRONG)]
    [InlineData(ObjectHandle.VHT_PINNED)]
    public void VariableHandleTypeHelpersUseExtraInfo(uint type)
    {
        const uint VariableType = (uint)HandleType.HNDTYPE_VARIABLE;
        uint* typeFlags = stackalloc uint[(int)VariableType + 1];
        typeFlags[VariableType] = HandleTableConstants.HNDF_EXTRAINFO;
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, VariableType + 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(
                table,
                VariableType,
                (byte*)0x1234,
                ObjectHandle.VHT_WEAK_SHORT);
            Assert.Equal(ObjectHandle.VHT_WEAK_SHORT, ObjectHandle.GetVariableHandleType(handle));

            ObjectHandle.UpdateVariableHandleType(handle, type);
            Assert.Equal(type, ObjectHandle.GetVariableHandleType(handle));

            Assert.Equal(
                type,
                ObjectHandle.CompareExchangeVariableHandleType(
                    handle,
                    type,
                    ObjectHandle.VHT_STRONG));
            Assert.Equal(ObjectHandle.VHT_STRONG, ObjectHandle.GetVariableHandleType(handle));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void HandleMetadataAndContainmentFollowOwningSegment()
    {
        const uint Type = 0;

        uint* typeFlags = stackalloc uint[1];
        typeFlags[Type] = HandleTableConstants.HNDF_EXTRAINFO;
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        HandleTable* otherTable = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);
        Assert.True(otherTable != null);

        try
        {
            OBJECTHANDLE handle;
            Assert.Equal(1u, HandleTableCore.TableAllocBulkHandles(table, Type, &handle, 1));

            Assert.Equal(Type, HandleTableCore.HandleFetchType(handle));
            Assert.True(HandleTableManager.HndGetHandleTable(handle) == table);
            Assert.True(HandleTableCore.TableContainHandle(table, handle));
            Assert.False(HandleTableCore.TableContainHandle(otherTable, handle));

            HandleTableManager.HndSetHandleExtraInfo(handle, Type, 0x1234);
            Assert.Equal((nuint)0x1234, HandleTableManager.HndGetHandleExtraInfo(handle));
            Assert.Equal(
                (nuint)0x1234,
                HandleTableManager.HndCompareExchangeHandleExtraInfo(handle, Type, 0x1234, 0x5678));
            Assert.Equal((nuint)0x5678, HandleTableManager.HndGetHandleExtraInfo(handle));
            Assert.Equal(
                (nuint)0x5678,
                HandleTableManager.HndCompareExchangeHandleExtraInfo(handle, Type, 0x1234, 0x9ABC));
            Assert.Equal((nuint)0x5678, HandleTableManager.HndGetHandleExtraInfo(handle));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(otherTable);
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void HandleCountExcludesReserveFreeAndQuickCaches()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);

            Assert.Equal(1u, HandleTableManager.HndCountHandles(table));

            HandleTableCache.TableFreeSingleHandleToCache(table, 0, handle);

            Assert.Equal(0u, HandleTableManager.HndCountHandles(table));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void CreateHandlePublishesReferentExtraInfoAndUpdatesClumpAge()
    {
        const uint Type = 0;
        const nuint ExtraInfo = 0x1234;
        uint* typeFlags = stackalloc uint[1];
        typeFlags[Type] = HandleTableConstants.HNDF_EXTRAINFO;
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            ManagedGCHeap.TestGeneration = 1;
            byte* obj = (byte*)0x5678;
            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(table, Type, obj, ExtraInfo);

            Assert.False(handle.IsNull);
            Assert.Equal((nuint)obj, GCEnv.VolatileLoad((nuint*)handle.Value));
            Assert.Equal(ExtraInfo, HandleTableManager.HndGetHandleExtraInfo(handle));

            TableSegment* segment = (TableSegment*)HandleTableCore.HandleFetchSegmentPointer(handle);
            nuint handleOrdinal = (((nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK)
                - HandleTableConstants.HANDLE_HEADER_SIZE) / (nuint)IntPtr.Size;
            nuint clump = handleOrdinal / 16;
            Assert.Equal(0, segment->Header.rgGeneration[(int)clump]);
            Assert.Equal(1u, HandleTableManager.HndCountHandles(table));
        }
        finally
        {
            ManagedGCHeap.TestGeneration = 0;
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Theory]
    [InlineData(0u, 2u, 1, 1)]
    [InlineData(0u, int.MaxValue, 3, 0)]
    [InlineData((uint)HandleType.HNDTYPE_DEPENDENT, 2u, 1, 0)]
    [InlineData((uint)HandleType.HNDTYPE_ASYNCPINNED, 2u, 1, 0)]
    public void WriteBarrierRecordsConvertedAndSpecialHandleAges(
        uint type,
        uint generation,
        int initialClumpAge,
        int expectedClumpAge)
    {
        uint typeCount = type + 1;
        uint* typeFlags = stackalloc uint[(int)typeCount];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, typeCount);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(table, type);
            TableSegment* segment = (TableSegment*)HandleTableCore.HandleFetchSegmentPointer(handle);
            nuint handleOrdinal = (((nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK)
                - HandleTableConstants.HANDLE_HEADER_SIZE) / (nuint)IntPtr.Size;
            nuint clump = handleOrdinal / 16;
            segment->Header.rgGeneration[(int)clump] = (byte)initialClumpAge;
            ManagedGCHeap.TestGeneration = generation;

            HandleTableManager.HndAssignHandle(handle, (byte*)0x5678);

            Assert.Equal(expectedClumpAge, segment->Header.rgGeneration[(int)clump]);
        }
        finally
        {
            ManagedGCHeap.TestGeneration = 0;
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void CreateNullHandleDoesNotUpdateClumpAge()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(table, 0, null, 0);
            Assert.False(handle.IsNull);

            TableSegment* segment = (TableSegment*)HandleTableCore.HandleFetchSegmentPointer(handle);
            nuint handleOrdinal = (((nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK)
                - HandleTableConstants.HANDLE_HEADER_SIZE) / (nuint)IntPtr.Size;
            nuint clump = handleOrdinal / 16;
            Assert.Equal(byte.MaxValue, segment->Header.rgGeneration[(int)clump]);
            Assert.Equal((nuint)0, GCEnv.VolatileLoad((nuint*)handle.Value));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void AssignHandleFiresEnabledSetEvent()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);
            GCToEEInterface.Reset();
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GCHandle, GCEventLevel.Information);
            GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);

            HandleTableManager.HndAssignHandle(handle, (byte*)0x5678);

            Assert.Equal(GCToEEInterface.FiredEvent.SetGCHandle, GCToEEInterface.LastFiredEvent);
        }
        finally
        {
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
            GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void AssignHandleGCSkipsSetEvent()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);
            GCToEEInterface.Reset();
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GCHandle, GCEventLevel.Information);

            HandleTableManager.HndAssignHandleGC(handle, (byte*)0x5678);

            Assert.Equal((nuint)0x5678, GCEnv.VolatileLoad((nuint*)handle.Value));
            Assert.Equal(GCToEEInterface.FiredEvent.None, GCToEEInterface.LastFiredEvent);
        }
        finally
        {
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompareExchangePublishesOnlyOnMatchingComparand(bool comparandMatches)
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            byte* original = (byte*)0x1234;
            byte* replacement = (byte*)0x5678;
            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(table, 0, original, 0);
            TableSegment* segment = (TableSegment*)HandleTableCore.HandleFetchSegmentPointer(handle);
            nuint handleOrdinal = (((nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK)
                - HandleTableConstants.HANDLE_HEADER_SIZE) / (nuint)IntPtr.Size;
            nuint clump = handleOrdinal / 16;
            segment->Header.rgGeneration[(int)clump] = 1;
            ManagedGCHeap.TestGeneration = 0;
            GCToEEInterface.Reset();
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GCHandle, GCEventLevel.Information);

            byte* comparand = comparandMatches ? original : (byte*)0x9ABC;
            byte* result = HandleTableManager.HndInterlockedCompareExchangeHandle(
                handle,
                replacement,
                comparand);

            Assert.Equal((nuint)original, (nuint)result);
            Assert.Equal(
                comparandMatches ? (nuint)replacement : (nuint)original,
                GCEnv.VolatileLoad((nuint*)handle.Value));
            Assert.Equal(0, segment->Header.rgGeneration[(int)clump]);
            Assert.Equal(
                comparandMatches ? GCToEEInterface.FiredEvent.SetGCHandle : GCToEEInterface.FiredEvent.None,
                GCToEEInterface.LastFiredEvent);
        }
        finally
        {
            ManagedGCHeap.TestGeneration = 0;
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstAssignPublishesBarrierAndEventOnlyForEmptyHandle(bool startsEmpty)
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            byte* original = startsEmpty ? null : (byte*)0x1234;
            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(table, 0, original, 0);
            TableSegment* segment = (TableSegment*)HandleTableCore.HandleFetchSegmentPointer(handle);
            nuint handleOrdinal = (((nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK)
                - HandleTableConstants.HANDLE_HEADER_SIZE) / (nuint)IntPtr.Size;
            nuint clump = handleOrdinal / 16;
            segment->Header.rgGeneration[(int)clump] = 1;
            ManagedGCHeap.TestGeneration = 0;
            GCToEEInterface.Reset();
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GCHandle, GCEventLevel.Information);

            byte success = HandleTableManager.HndFirstAssignHandle(handle, (byte*)0x5678);

            Assert.Equal(startsEmpty ? 1 : 0, success);
            Assert.Equal(
                startsEmpty ? (nuint)0x5678 : (nuint)original,
                GCEnv.VolatileLoad((nuint*)handle.Value));
            Assert.Equal(startsEmpty ? 0 : 1, segment->Header.rgGeneration[(int)clump]);
            Assert.Equal(
                startsEmpty ? GCToEEInterface.FiredEvent.SetGCHandle : GCToEEInterface.FiredEvent.None,
                GCToEEInterface.LastFiredEvent);
        }
        finally
        {
            ManagedGCHeap.TestGeneration = 0;
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void DependentSecondaryUsesExtraInfoAndPrimaryClumpBarrier()
    {
        const uint Type = (uint)HandleType.HNDTYPE_DEPENDENT;
        uint* typeFlags = stackalloc uint[(int)Type + 1];
        typeFlags[Type] = HandleTableConstants.HNDF_EXTRAINFO;
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, Type + 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(table, Type, (byte*)0x1234, 0);
            TableSegment* segment = (TableSegment*)HandleTableCore.HandleFetchSegmentPointer(handle);
            nuint handleOrdinal = (((nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK)
                - HandleTableConstants.HANDLE_HEADER_SIZE) / (nuint)IntPtr.Size;
            nuint clump = handleOrdinal / 16;
            segment->Header.rgGeneration[(int)clump] = 1;
            ManagedGCHeap.TestGeneration = 2;

            HandleTableManager.SetDependentHandleSecondary(handle, (byte*)0x5678);

            Assert.Equal((nuint)0x5678, (nuint)HandleTableManager.GetDependentHandleSecondary(handle));
            Assert.Equal(0, segment->Header.rgGeneration[(int)clump]);
        }
        finally
        {
            ManagedGCHeap.TestGeneration = 0;
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void CreateHandleReusesDestroyedDebugHandle()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE first = HandleTableManager.HndCreateHandle(table, 0, (byte*)0x1234, 0);
            HandleTableManager.HndDestroyHandle(table, 0, first);

            OBJECTHANDLE second = HandleTableManager.HndCreateHandle(table, 0, (byte*)0x5678, 0);

            Assert.True(first.Value == second.Value);
            Assert.Equal((nuint)0x5678, *(nuint*)second.Value);
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void TypedAndUnknownTypeDestructionReturnHandlesToCache()
    {
        uint* typeFlags = stackalloc uint[2];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 2);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE first = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);
            OBJECTHANDLE second = HandleTableCache.TableAllocSingleHandleFromCache(table, 1);
            *(nuint*)first.Value = 0x1234;
            *(nuint*)second.Value = 0x5678;

            HandleTableManager.HndDestroyHandle(table, 0, first);
            HandleTableManager.HndDestroyHandleOfUnknownType(table, second);

            Assert.Equal(0u, HandleTableManager.HndCountHandles(table));
            Assert.Equal((nuint)first.Value, (nuint)((OBJECTHANDLE*)table->rgQuickCache)[0].Value);
            Assert.Equal((nuint)second.Value, (nuint)((OBJECTHANDLE*)table->rgQuickCache)[1].Value);
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void DestroyHandleFiresEnabledEvent()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableManager.HndCreateHandle(table, 0, (byte*)0x1234, 0);
            GCToEEInterface.Reset();
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GCHandle, GCEventLevel.Information);

            HandleTableManager.HndDestroyHandle(table, 0, handle);

            Assert.Equal(GCToEEInterface.FiredEvent.DestroyGCHandle, GCToEEInterface.LastFiredEvent);
        }
        finally
        {
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpreparedBulkFreeSortsClearsAndFreesMoreThanOneBlock(bool failLargeScratchAllocation)
    {
        const uint Count = 130;

        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[(int)Count];
            Assert.Equal(Count, HandleTableCore.TableAllocBulkHandles(table, 0, handles, Count));

            for (int i = 0; i < Count; i++)
            {
                *(nuint*)handles[i].Value = (nuint)(i + 1);
            }

            for (int i = 0; i < Count / 2; i++)
            {
                OBJECTHANDLE temporary = handles[i];
                handles[i] = handles[Count - i - 1];
                handles[Count - i - 1] = temporary;
            }

            SyncImports.FailNextAlloc = failLargeScratchAllocation;
            HandleTableCore.TableFreeBulkUnpreparedHandles(table, 0, handles, Count);

            Assert.False(SyncImports.FailNextAlloc);
            Assert.Equal(0u, table->dwCount);
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal((nuint)0, *(nuint*)handles[i].Value);
            }
        }
        finally
        {
            SyncImports.FailNextAlloc = false;
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void TableSegmentHeaderMatchesThePackedNativeFieldOrder()
    {
        int offset = 0;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgGeneration), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT * sizeof(uint);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgAllocation), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgFreeMask), offset);
        offset += HandleTableConstants.HANDLE_MASKS_PER_SEGMENT * sizeof(uint);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgBlockType), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgUserData), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgLocks), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgTail), offset);
        offset += HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgHint), offset);
        offset += HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgFreeCount), offset);
        offset += HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES * sizeof(uint);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.pNextSegment), offset);
        offset += IntPtr.Size;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.pHandleTable), offset);
        offset += IntPtr.Size;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.flags), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bFreeList), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bEmptyLine), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bCommitLine), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bDecommitLine), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bSequence), offset++);
        Assert.Equal(HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE, offset);
        Assert.Equal(HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE, sizeof(_TableSegmentHeader));
    }

    [Fact]
    public void TableSegmentIsExactlyOneAlignedNativeSegment()
    {
        AssertOffset<TableSegment>(nameof(TableSegment.Header), 0);
        AssertOffset<TableSegment>(nameof(TableSegment.rgUnused), HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE);
        AssertOffset<TableSegment>(nameof(TableSegment.rgValue), HandleTableConstants.HANDLE_HEADER_SIZE);
        Assert.Equal(HandleTableConstants.HANDLE_SEGMENT_SIZE, sizeof(TableSegment));
    }

    [Fact]
    public void HandleTypeCacheKeepsItsIndicesInDifferentCacheLines()
    {
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.rgReserveBank), 0);
        int reserveIndex = HandleTableConstants.HANDLES_PER_CACHE_BANK * IntPtr.Size;
        int freeBank = AlignUp(reserveIndex + sizeof(int), IntPtr.Size);
        int freeIndex = freeBank + (HandleTableConstants.HANDLES_PER_CACHE_BANK * IntPtr.Size);
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.lReserveIndex), reserveIndex);
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.rgFreeBank), freeBank);
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.lFreeIndex), freeIndex);
        Assert.Equal(AlignUp(freeIndex + sizeof(int), IntPtr.Size), sizeof(HandleTypeCache));
    }

    [Fact]
    public void DacHandleTypesMatchTheirNativeAnalogues()
    {
        int nextSegment = HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE - (2 * IntPtr.Size) - 6;
        AssertOffset<dac_handle_table_segment>(nameof(dac_handle_table_segment.rgGeneration), 0);
        AssertOffset<dac_handle_table_segment>(nameof(dac_handle_table_segment.pNextSegment), nextSegment);
        Assert.Equal(nextSegment + IntPtr.Size, sizeof(dac_handle_table_segment));

        AssertOffset<dac_handle_table>(nameof(dac_handle_table.padding), 0);
        int segmentList = AlignUp(HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES * sizeof(uint), IntPtr.Size);
        AssertOffset<dac_handle_table>(nameof(dac_handle_table.pSegmentList), segmentList);
        Assert.Equal(segmentList + IntPtr.Size, sizeof(dac_handle_table));

        AssertOffset<dac_handle_table_bucket>(nameof(dac_handle_table_bucket.pTable), 0);
        AssertOffset<dac_handle_table_bucket>(nameof(dac_handle_table_bucket.HandleTableIndex), IntPtr.Size);
        Assert.Equal(AlignUp(IntPtr.Size + sizeof(uint), IntPtr.Size), sizeof(dac_handle_table_bucket));
    }

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : unmanaged
    {
        Assert.Equal((nint)expected, Marshal.OffsetOf<T>(fieldName));
    }

    private static int AlignUp(int value, int alignment) =>
        (value + (alignment - 1)) & ~(alignment - 1);
}
