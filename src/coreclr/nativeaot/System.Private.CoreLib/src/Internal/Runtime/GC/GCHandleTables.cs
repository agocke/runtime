// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GC
{
    internal static unsafe class GCHandleTables
    {
        private const int InitialHandleTableArraySize = 10;
        private const int HandleMaxInternalTypes = 13;
        private const int HandleMaxPublicTypes = 12;
        private const int HandleSegmentSize = 0x10000;
        private const int HandleHeaderSize = 0x1000;
        private const int HandleHandlesPerBlock = 64;
        private const int HandleSlotSize = 8;
        private const int HandleBlockSize = HandleSlotSize * HandleHandlesPerBlock;
        private const int HandleBlocksPerSegment = (HandleSegmentSize - HandleHeaderSize) / HandleBlockSize;
        private const int HandlesPerCacheBank = 63;
        private const byte TypeInvalid = 0xFF;
        private const byte BlockInvalid = 0xFF;
        private const byte InternalDataBlockType = HandleMaxPublicTypes;
        private const uint HandleTableCount = 1;
        private const uint VHT_WEAK_SHORT = 0x00000100;
        private const uint VHT_WEAK_LONG = 0x00000200;
        private const uint VHT_STRONG = 0x00000400;
        private const uint VHT_PINNED = 0x00000800;

        private static IGCHandleStoreVtable s_storeVtable;
        private static GCHandleStore s_globalStore;
        private static HandleTableMap s_handleTableMap;
        private static byte s_initialized;

        [StructLayout(LayoutKind.Sequential)]
        private struct GCHandleStore
        {
            public IGCHandleStoreVtable* Vtable;
            public HandleTableBucket UnderlyingBucket;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleTableBucket
        {
            public HandleTable** Tables;
            public uint HandleTableIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleTableMap
        {
            public HandleTableBucket** Buckets;
            public HandleTableMap* Next;
            public uint MaxIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleTypeCache
        {
            public fixed ulong ReserveBank[HandlesPerCacheBank];
            public int ReserveIndex;
            public fixed ulong FreeBank[HandlesPerCacheBank];
            public int FreeIndex;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct HandleTableSegment
        {
            public fixed byte Generation[HandleBlocksPerSegment * 4];
            public fixed byte Allocation[HandleBlocksPerSegment];
            public fixed uint FreeMask[HandleBlocksPerSegment * 2];
            public fixed byte BlockType[HandleBlocksPerSegment];
            public fixed byte UserData[HandleBlocksPerSegment];
            public fixed byte Locks[HandleBlocksPerSegment];
            public fixed byte Tail[HandleMaxInternalTypes];
            public fixed byte Hint[HandleMaxInternalTypes];
            public fixed uint FreeCount[HandleMaxInternalTypes];
            public HandleTableSegment* NextSegment;
            public HandleTable* OwnerTable;
            public byte Flags;
            public byte FreeList;
            public byte EmptyLine;
            public byte CommitLine;
            public byte DecommitLine;
            public byte Sequence;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleTable
        {
            public fixed uint TypeFlags[HandleMaxInternalTypes];
            public HandleTableSegment* SegmentList;
            public CrstStatic Lock;
            public uint TypeCount;
            public uint HandleCount;
            public void* AsyncScanInfo;
            public uint TableIndex;
            public fixed ulong QuickCache[HandleMaxInternalTypes];
        }

        public static bool Initialize()
        {
            if (s_initialized != 0)
            {
                return true;
            }

            if (HandleBlocksPerSegment <= 0 || sizeof(HandleTableSegment) > HandleHeaderSize)
            {
                return false;
            }

            s_storeVtable = default;
            s_storeVtable.Uproot = &Uproot;
            s_storeVtable.ContainsHandle = &ContainsHandle;
            s_storeVtable.CreateHandleOfType = &CreateHandleOfType;
            s_storeVtable.CreateHandleOfTypeWithHeapAffinity = &CreateHandleOfTypeWithHeapAffinity;
            s_storeVtable.CreateHandleWithExtraInfo = &CreateHandleWithExtraInfo;
            s_storeVtable.CreateDependentHandle = &CreateDependentHandle;
            s_storeVtable.CompleteObjectDestructor = &CompleteObjectDestructor;
            s_storeVtable.DeletingDestructor = &DeletingDestructor;
            fixed (IGCHandleStoreVtable* storeVtable = &s_storeVtable)
            fixed (GCHandleStore* globalStore = &s_globalStore)
            {
                globalStore->Vtable = storeVtable;
            }

            HandleTable** tables = (HandleTable**)GCUnixSyncImports.malloc((nuint)sizeof(HandleTable*));
            if (tables is null)
            {
                return false;
            }

            HandleTable* table = CreateHandleTable();
            if (table is null)
            {
                GCUnixSyncImports.free(tables);
                return false;
            }

            tables[0] = table;
            s_globalStore.UnderlyingBucket.Tables = tables;
            s_globalStore.UnderlyingBucket.HandleTableIndex = 0;

            nuint bucketArraySize = (nuint)InitialHandleTableArraySize * (nuint)sizeof(HandleTableBucket*);
            HandleTableBucket** buckets = (HandleTableBucket**)GCUnixSyncImports.malloc(bucketArraySize);
            if (buckets is null)
            {
                DestroyHandleTable(table);
                GCUnixSyncImports.free(tables);
                s_globalStore.UnderlyingBucket.Tables = null;
                return false;
            }

            NativeMemoryClear(buckets, bucketArraySize);
            fixed (HandleTableBucket* bucket = &s_globalStore.UnderlyingBucket)
            {
                buckets[0] = bucket;
            }

            s_handleTableMap.Buckets = buckets;
            s_handleTableMap.Next = null;
            s_handleTableMap.MaxIndex = InitialHandleTableArraySize;
            s_initialized = 1;
            return true;
        }

        public static IGCHandleStore* GetGlobalHandleStore()
        {
            fixed (GCHandleStore* store = &s_globalStore)
            {
                return (IGCHandleStore*)store;
            }
        }

        public static bool Shutdown()
        {
            if (s_initialized == 0)
            {
                return true;
            }

            fixed (GCHandleStore* globalStore = &s_globalStore)
            {
                HandleTableBucket* bucket = &globalStore->UnderlyingBucket;
                if (bucket->Tables is not null)
                {
                    for (uint i = 0; i < HandleTableCount; i++)
                    {
                        if (bucket->Tables[i] is not null)
                        {
                            DestroyHandleTable(bucket->Tables[i]);
                        }
                    }

                    GCUnixSyncImports.free(bucket->Tables);
                    bucket->Tables = null;
                }
            }

            if (s_handleTableMap.Buckets is not null)
            {
                GCUnixSyncImports.free(s_handleTableMap.Buckets);
                s_handleTableMap.Buckets = null;
            }

            s_handleTableMap = default;
            s_globalStore = default;
            s_storeVtable = default;
            s_initialized = 0;
            return true;
        }

        public static bool CreateHandleStore(IGCHandleStore** store)
        {
            if (store is not null)
            {
                *store = null;
            }

            return false;
        }

        public static void DestroyHandleStore(IGCHandleStore* store)
        {
            if (store is null || store == GetGlobalHandleStore())
            {
                return;
            }

            GCHandleStore* handleStore = (GCHandleStore*)store;
            DestroyBucket(&handleStore->UnderlyingBucket);
            GCUnixSyncImports.free(handleStore);
        }

        private static void Uproot(IGCHandleStore* store)
        {
            _ = store;
            FailFast();
        }

        private static bool ContainsHandle(IGCHandleStore* store, OBJECTHANDLE__* handle)
        {
            _ = store;
            _ = handle;
            FailFast();
            return false;
        }

        private static OBJECTHANDLE__* CreateHandleOfType(IGCHandleStore* store, Object* value, HandleType type)
        {
            return CreateHandle((GCHandleStore*)store, value, type, null);
        }

        private static OBJECTHANDLE__* CreateHandleOfTypeWithHeapAffinity(IGCHandleStore* store, Object* value, HandleType type, int heap)
        {
            _ = heap;
            return CreateHandle((GCHandleStore*)store, value, type, null);
        }

        private static OBJECTHANDLE__* CreateHandleWithExtraInfo(IGCHandleStore* store, Object* value, HandleType type, void* extraInfo)
        {
            return CreateHandle((GCHandleStore*)store, value, type, extraInfo);
        }

        private static OBJECTHANDLE__* CreateDependentHandle(IGCHandleStore* store, Object* primary, Object* secondary)
        {
            OBJECTHANDLE__* handle = CreateHandle((GCHandleStore*)store, primary, HandleType.HNDTYPE_DEPENDENT, null);
            if (handle is not null)
            {
                SetDependentHandleSecondaryCore(handle, secondary);
            }

            return handle;
        }

        private static void CompleteObjectDestructor(IGCHandleStore* store)
        {
            if (store is not null)
            {
                DestroyBucket(&((GCHandleStore*)store)->UnderlyingBucket);
            }
        }

        private static void DeletingDestructor(IGCHandleStore* store)
        {
            if (store is not null)
            {
                GCHandleStore* handleStore = (GCHandleStore*)store;
                DestroyBucket(&handleStore->UnderlyingBucket);
                if (store != GetGlobalHandleStore())
                {
                    GCUnixSyncImports.free(handleStore);
                }
            }
        }

        public static bool ManagerInitialize(IGCHandleManager* manager)
        {
            _ = manager;
            return Initialize();
        }

        public static void ManagerShutdown(IGCHandleManager* manager)
        {
            _ = manager;
            Shutdown();
        }

        public static IGCHandleStore* ManagerGetGlobalHandleStore(IGCHandleManager* manager)
        {
            _ = manager;
            return GetGlobalHandleStore();
        }

        public static IGCHandleStore* ManagerCreateHandleStore(IGCHandleManager* manager)
        {
            _ = manager;
            return null;
        }

        public static void ManagerDestroyHandleStore(IGCHandleManager* manager, IGCHandleStore* store)
        {
            _ = manager;
            DestroyHandleStore(store);
        }

        public static OBJECTHANDLE__* ManagerCreateGlobalHandleOfType(IGCHandleManager* manager, Object* value, HandleType type)
        {
            _ = manager;
            return CreateHandle((GCHandleStore*)GetGlobalHandleStore(), value, type, null);
        }

        public static OBJECTHANDLE__* ManagerCreateDuplicateHandle(IGCHandleManager* manager, OBJECTHANDLE__* handle)
        {
            _ = manager;
            if (handle is null)
            {
                return null;
            }

            return CreateHandleOnTable(GetHandleTable(handle), FetchObject(handle), HandleType.HNDTYPE_DEFAULT, null);
        }

        public static void ManagerDestroyHandleOfType(IGCHandleManager* manager, OBJECTHANDLE__* handle, HandleType type)
        {
            _ = manager;
            DestroyHandle(handle, type);
        }

        public static void ManagerDestroyHandleOfUnknownType(IGCHandleManager* manager, OBJECTHANDLE__* handle)
        {
            _ = manager;
            if (handle is not null)
            {
                DestroyHandle(handle, FetchType(handle));
            }
        }

        public static void ManagerSetExtraInfoForHandle(IGCHandleManager* manager, OBJECTHANDLE__* handle, HandleType type, void* extraInfo)
        {
            _ = manager;
            void** slot = FetchExtraInfoSlot(handle, type);
            if (slot is not null)
            {
                Interlocked.ExchangePointer((nint*)slot, extraInfo);
            }
        }

        public static void* ManagerGetExtraInfoFromHandle(IGCHandleManager* manager, OBJECTHANDLE__* handle)
        {
            _ = manager;
            void** slot = FetchExtraInfoSlot(handle, FetchType(handle));
            return slot is null ? null : *slot;
        }

        public static void ManagerStoreObjectInHandle(IGCHandleManager* manager, OBJECTHANDLE__* handle, Object* value)
        {
            _ = manager;
            StoreObject(handle, value);
        }

        public static bool ManagerStoreObjectInHandleIfNull(IGCHandleManager* manager, OBJECTHANDLE__* handle, Object* value)
        {
            _ = manager;
            if (handle is null)
            {
                return false;
            }

            if (!EnsureHandleCommitted(handle))
            {
                FailFast();
                return false;
            }

            nint* slot = (nint*)handle;
            return Interlocked.CompareExchangePointer(slot, value, null) == 0;
        }

        public static void ManagerSetDependentHandleSecondary(IGCHandleManager* manager, OBJECTHANDLE__* handle, Object* value)
        {
            _ = manager;
            SetDependentHandleSecondaryCore(handle, value);
        }

        public static Object* ManagerGetDependentHandleSecondary(IGCHandleManager* manager, OBJECTHANDLE__* handle)
        {
            _ = manager;
            void** slot = FetchExtraInfoSlot(handle, HandleType.HNDTYPE_DEPENDENT);
            return slot is null ? null : (Object*)*slot;
        }

        public static Object* ManagerInterlockedCompareExchangeObjectInHandle(
            IGCHandleManager* manager,
            OBJECTHANDLE__* handle,
            Object* value,
            Object* comparand)
        {
            _ = manager;
            if (handle is null)
            {
                return null;
            }

            if (!EnsureHandleCommitted(handle))
            {
                FailFast();
                return null;
            }

            return (Object*)Interlocked.CompareExchangePointer((nint*)handle, value, comparand);
        }

        public static HandleType ManagerHandleFetchType(IGCHandleManager* manager, OBJECTHANDLE__* handle)
        {
            _ = manager;
            return FetchType(handle);
        }

        public static void ManagerTraceRefCountedHandles(
            IGCHandleManager* manager,
            delegate* unmanaged[SuppressGCTransition]<Object**, nuint*, nuint, nuint, void> callback,
            nuint param1,
            nuint param2)
        {
            _ = manager;
            if (callback is null)
            {
                return;
            }

            ScanRefCountedHandles(callback, param1, param2);
        }

        public static void ScanForPromotion(
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            int condemnedGeneration,
            int maxGeneration)
        {
            ScanHandleType(HandleType.HNDTYPE_PINNED, callback, scanContext, (uint)GCInterfaceConstants.GC_CALL_PINNED);
            ScanAsyncPinnedHandles(callback, scanContext);
            ScanVariableHandles(callback, scanContext, VHT_PINNED, (uint)GCInterfaceConstants.GC_CALL_PINNED);

            ScanHandleType(HandleType.HNDTYPE_STRONG, callback, scanContext, 0);
            if (condemnedGeneration < maxGeneration)
            {
                ScanHandleType(HandleType.HNDTYPE_SIZEDREF, callback, scanContext, 0);
            }

            ScanVariableHandles(callback, scanContext, VHT_STRONG, 0);

            IGCToCLR* gcToClr = GCCommon.g_theGCToCLR;
            if (gcToClr is not null &&
                gcToClr->Vtable is not null &&
                gcToClr->Vtable->RefCountedHandleCallbacks is not null)
            {
                ScanRefCountedHandlesForPromotion(callback, scanContext, gcToClr);
            }
        }

        public static void ScanSizedRefForPromotion(
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext)
        {
            ScanHandleType(HandleType.HNDTYPE_SIZEDREF, callback, scanContext, 0);
        }

        public static void ClearUnpromotedHandles(HandleType type)
        {
            ClearUnpromotedHandleType(type);
            if (type == HandleType.HNDTYPE_WEAK_SHORT)
            {
                ClearUnpromotedVariableHandles(VHT_WEAK_SHORT);
            }

            if (type == HandleType.HNDTYPE_WEAK_LONG)
            {
                ClearUnpromotedHandleType(HandleType.HNDTYPE_REFCOUNTED);
                ClearUnpromotedHandleType(HandleType.HNDTYPE_WEAK_INTERIOR_POINTER);
                ClearUnpromotedVariableHandles(VHT_WEAK_LONG);
            }
        }

        public static void ClearUnpromotedDependentHandles()
        {
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                ClearUnpromotedDependentBlocks(segment);
                            }
                        }
                    }
                }
            }
        }

        private static void ScanHandleType(
            HandleType type,
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            uint flags)
        {
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                ScanBlock(segment, type, callback, scanContext, flags);
                            }
                        }
                    }
                }
            }
        }

        private static void ScanBlock(
            HandleTableSegment* segment,
            HandleType type,
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            uint flags)
        {
            for (int block = 0; block < HandleBlocksPerSegment; block++)
            {
                if (segment->BlockType[block] != (byte)type)
                {
                    continue;
                }

                if (!EnsureBlockCommitted(segment, block))
                {
                    FailFast();
                    return;
                }

                uint lowerMask = segment->FreeMask[block * 2];
                uint upperMask = segment->FreeMask[(block * 2) + 1];
                for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                {
                    bool free = slot < 32
                        ? (lowerMask & (1u << slot)) != 0
                        : (upperMask & (1u << (slot - 32))) != 0;
                    if (!free)
                    {
                        OBJECTHANDLE__* handle = SlotAddress(segment, block, slot);
                        callback((Object**)handle, scanContext, flags);
                    }
                }
            }
        }

        private static void ScanVariableHandles(
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            uint variableType,
            uint flags)
        {
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                ScanVariableBlocks(segment, callback, scanContext, variableType, flags);
                            }
                        }
                    }
                }
            }
        }

        private static void ScanVariableBlocks(
            HandleTableSegment* segment,
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            uint variableType,
            uint flags)
        {
            for (int block = 0; block < HandleBlocksPerSegment; block++)
            {
                if (segment->BlockType[block] != (byte)HandleType.HNDTYPE_VARIABLE ||
                    !EnsureBlockCommitted(segment, block))
                {
                    continue;
                }

                void** extraInfo = null;
                byte dataBlock = segment->UserData[block];
                if (dataBlock != BlockInvalid && EnsureBlockCommitted(segment, dataBlock))
                {
                    extraInfo = (void**)SlotAddress(segment, dataBlock, 0);
                }

                uint lowerMask = segment->FreeMask[block * 2];
                uint upperMask = segment->FreeMask[(block * 2) + 1];
                for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                {
                    bool free = slot < 32
                        ? (lowerMask & (1u << slot)) != 0
                        : (upperMask & (1u << (slot - 32))) != 0;
                    if (free || extraInfo is null || (((nuint)extraInfo[slot] & variableType) == 0))
                    {
                        continue;
                    }

                    callback((Object**)SlotAddress(segment, block, slot), scanContext, flags);
                }
            }
        }

        private static void ScanRefCountedHandles(
            delegate* unmanaged[SuppressGCTransition]<Object**, nuint*, nuint, nuint, void> callback,
            nuint param1,
            nuint param2)
        {
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                for (int block = 0; block < HandleBlocksPerSegment; block++)
                                {
                                    if (segment->BlockType[block] != (byte)HandleType.HNDTYPE_REFCOUNTED)
                                    {
                                        continue;
                                    }

                                    if (!EnsureBlockCommitted(segment, block))
                                    {
                                        FailFast();
                                        return;
                                    }

                                    uint lowerMask = segment->FreeMask[block * 2];
                                    uint upperMask = segment->FreeMask[(block * 2) + 1];
                                    for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                                    {
                                        bool free = slot < 32
                                            ? (lowerMask & (1u << slot)) != 0
                                            : (upperMask & (1u << (slot - 32))) != 0;
                                        if (!free)
                                        {
                                            callback((Object**)SlotAddress(segment, block, slot), null, param1, param2);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void ScanRefCountedHandlesForPromotion(
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            IGCToCLR* gcToClr)
        {
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                for (int block = 0; block < HandleBlocksPerSegment; block++)
                                {
                                    if (segment->BlockType[block] != (byte)HandleType.HNDTYPE_REFCOUNTED)
                                    {
                                        continue;
                                    }

                                    if (!EnsureBlockCommitted(segment, block))
                                    {
                                        FailFast();
                                        return;
                                    }

                                    uint lowerMask = segment->FreeMask[block * 2];
                                    uint upperMask = segment->FreeMask[(block * 2) + 1];
                                    for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                                    {
                                        bool free = slot < 32
                                            ? (lowerMask & (1u << slot)) != 0
                                            : (upperMask & (1u << (slot - 32))) != 0;
                                        if (free)
                                        {
                                            continue;
                                        }

                                        OBJECTHANDLE__* handle = SlotAddress(segment, block, slot);
                                        Object* obj = FetchObject(handle);
                                        if (obj is not null &&
                                            gcToClr->Vtable->RefCountedHandleCallbacks(gcToClr, obj))
                                        {
                                            callback((Object**)handle, scanContext, 0);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void ScanAsyncPinnedHandles(
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext)
        {
            IGCToCLR* gcToClr = GCCommon.g_theGCToCLR;
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                for (int block = 0; block < HandleBlocksPerSegment; block++)
                                {
                                    if (segment->BlockType[block] != (byte)HandleType.HNDTYPE_ASYNCPINNED)
                                    {
                                        continue;
                                    }

                                    ScanAsyncPinnedBlock(segment, block, callback, scanContext, gcToClr);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void ScanAsyncPinnedBlock(
            HandleTableSegment* segment,
            int block,
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            IGCToCLR* gcToClr)
        {
            if (!EnsureBlockCommitted(segment, block))
            {
                FailFast();
                return;
            }

            uint lowerMask = segment->FreeMask[block * 2];
            uint upperMask = segment->FreeMask[(block * 2) + 1];
            for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
            {
                bool free = slot < 32
                    ? (lowerMask & (1u << slot)) != 0
                    : (upperMask & (1u << (slot - 32))) != 0;
                if (!free)
                {
                    ScanAsyncPinnedHandle(
                        SlotAddress(segment, block, slot),
                        callback,
                        scanContext,
                        gcToClr);
                }
            }
        }

        private static void ScanAsyncPinnedHandle(
            OBJECTHANDLE__* handle,
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext,
            IGCToCLR* gcToClr)
        {
            callback((Object**)handle, scanContext, 0);
            Object* obj = FetchObject(handle);
            if (obj is not null &&
                gcToClr is not null &&
                gcToClr->Vtable is not null &&
                gcToClr->Vtable->WalkAsyncPinnedForPromotion is not null)
            {
                gcToClr->Vtable->WalkAsyncPinnedForPromotion(
                    gcToClr,
                    obj,
                    scanContext,
                    callback);
            }
        }

        private static void ClearUnpromotedHandleType(HandleType type)
        {
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                ClearUnpromotedBlocks(segment, type);
                            }
                        }
                    }
                }
            }
        }

        private static void ClearUnpromotedDependentBlocks(HandleTableSegment* segment)
        {
            for (int block = 0; block < HandleBlocksPerSegment; block++)
            {
                if (segment->BlockType[block] != (byte)HandleType.HNDTYPE_DEPENDENT ||
                    !EnsureBlockCommitted(segment, block))
                {
                    continue;
                }

                byte dataBlock = segment->UserData[block];
                if (dataBlock == BlockInvalid || !EnsureBlockCommitted(segment, dataBlock))
                {
                    FailFast();
                    return;
                }

                void** secondarySlots = (void**)SlotAddress(segment, dataBlock, 0);
                uint lowerMask = segment->FreeMask[block * 2];
                uint upperMask = segment->FreeMask[(block * 2) + 1];
                for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                {
                    bool free = slot < 32
                        ? (lowerMask & (1u << slot)) != 0
                        : (upperMask & (1u << (slot - 32))) != 0;
                    if (free)
                    {
                        continue;
                    }

                    OBJECTHANDLE__* handle = SlotAddress(segment, block, slot);
                    Object* primary = FetchObject(handle);
                    if (!GCWksInitialization.IsPromotedObject(primary))
                    {
                        StoreObject(handle, null);
                        secondarySlots[slot] = null;
                    }
                }
            }
        }

        private static void ClearUnpromotedVariableHandles(uint variableType)
        {
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                for (HandleTableMap* map = mapStorage; map is not null; map = map->Next)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList;
                                segment is not null;
                                segment = segment->NextSegment)
                            {
                                ClearUnpromotedVariableBlocks(segment, variableType);
                            }
                        }
                    }
                }
            }
        }

        private static void ClearUnpromotedVariableBlocks(HandleTableSegment* segment, uint variableType)
        {
            for (int block = 0; block < HandleBlocksPerSegment; block++)
            {
                if (segment->BlockType[block] != (byte)HandleType.HNDTYPE_VARIABLE ||
                    !EnsureBlockCommitted(segment, block))
                {
                    continue;
                }

                byte dataBlock = segment->UserData[block];
                if (dataBlock == BlockInvalid || !EnsureBlockCommitted(segment, dataBlock))
                {
                    FailFast();
                    return;
                }

                void** extraInfo = (void**)SlotAddress(segment, dataBlock, 0);
                uint lowerMask = segment->FreeMask[block * 2];
                uint upperMask = segment->FreeMask[(block * 2) + 1];
                for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                {
                    bool free = slot < 32
                        ? (lowerMask & (1u << slot)) != 0
                        : (upperMask & (1u << (slot - 32))) != 0;
                    Object* obj = FetchObject(SlotAddress(segment, block, slot));
                    if (!free &&
                        ((nuint)extraInfo[slot] & variableType) != 0 &&
                        obj is not null &&
                        !GCWksInitialization.IsPromotedObject(obj))
                    {
                        StoreObject(SlotAddress(segment, block, slot), null);
                    }
                }
            }
        }

        private static void ClearUnpromotedBlocks(HandleTableSegment* segment, HandleType type)
        {
            for (int block = 0; block < HandleBlocksPerSegment; block++)
            {
                if (segment->BlockType[block] != (byte)type || !EnsureBlockCommitted(segment, block))
                {
                    continue;
                }

                uint lowerMask = segment->FreeMask[block * 2];
                uint upperMask = segment->FreeMask[(block * 2) + 1];
                for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                {
                    bool free = slot < 32
                        ? (lowerMask & (1u << slot)) != 0
                        : (upperMask & (1u << (slot - 32))) != 0;
                    if (!free)
                    {
                        OBJECTHANDLE__* handle = SlotAddress(segment, block, slot);
                        Object* obj = FetchObject(handle);
                        if (obj is not null && !GCWksInitialization.IsPromotedObject(obj))
                        {
                            StoreObject(handle, null);
                        }
                    }
                }
            }
        }

        public static bool ScanDependentHandlesForPromotion(
            delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> callback,
            ScanContext* scanContext)
        {
            bool promoted = false;
            fixed (HandleTableMap* mapStorage = &s_handleTableMap)
            {
                HandleTableMap* map = mapStorage;
                while (map is not null)
                {
                    for (uint bucketIndex = 0; bucketIndex < InitialHandleTableArraySize; bucketIndex++)
                    {
                        HandleTableBucket* bucket = map->Buckets[bucketIndex];
                        if (bucket is null || bucket->Tables is null)
                        {
                            continue;
                        }

                        for (uint tableIndex = 0; tableIndex < HandleTableCount; tableIndex++)
                        {
                            HandleTable* table = bucket->Tables[tableIndex];
                            if (table is null)
                            {
                                continue;
                            }

                            for (HandleTableSegment* segment = table->SegmentList; segment is not null; segment = segment->NextSegment)
                            {
                                for (int block = 0; block < HandleBlocksPerSegment; block++)
                                {
                                    if (segment->BlockType[block] != (byte)HandleType.HNDTYPE_DEPENDENT)
                                    {
                                        continue;
                                    }

                                    if (!EnsureBlockCommitted(segment, block))
                                    {
                                        FailFast();
                                        return promoted;
                                    }

                                    uint lowerMask = segment->FreeMask[block * 2];
                                    uint upperMask = segment->FreeMask[(block * 2) + 1];
                                    for (int slot = 0; slot < HandleHandlesPerBlock; slot++)
                                    {
                                        bool free = slot < 32
                                            ? (lowerMask & (1u << slot)) != 0
                                            : (upperMask & (1u << (slot - 32))) != 0;
                                        if (free)
                                        {
                                            continue;
                                        }

                                        OBJECTHANDLE__* handle = SlotAddress(segment, block, slot);
                                        Object* primary = (Object*)*(nint*)handle;
                                        if (primary is null)
                                        {
                                            continue;
                                        }

                                        void** secondarySlot = FetchExtraInfoSlot(handle, HandleType.HNDTYPE_DEPENDENT);
                                        if (secondarySlot is null || *secondarySlot is null)
                                        {
                                            continue;
                                        }

                                        if (GCWksInitialization.IsPromotedObject(primary))
                                        {
                                            Object** secondary = (Object**)secondarySlot;
                                            bool wasPromoted = GCWksInitialization.IsPromotedObject((Object*)*secondarySlot);
                                            callback(secondary, scanContext, 0);
                                            promoted |= !wasPromoted && *secondary is not null &&
                                                GCWksInitialization.IsPromotedObject(*secondary);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    map = map->Next;
                }
            }

            return promoted;
        }

        private static OBJECTHANDLE__* CreateHandle(GCHandleStore* store, Object* value, HandleType type, void* extraInfo)
        {
            if (store is null || store->UnderlyingBucket.Tables is null || (uint)type >= HandleMaxPublicTypes)
            {
                return null;
            }

            HandleTable* table = store->UnderlyingBucket.Tables[0];
            return CreateHandleOnTable(table, value, type, extraInfo);
        }

        private static OBJECTHANDLE__* CreateHandleOnTable(HandleTable* table, Object* value, HandleType type, void* extraInfo)
        {
            if (table is null || (uint)type >= HandleMaxPublicTypes)
            {
                return null;
            }

            table->Lock.Enter();
            OBJECTHANDLE__* handle = AllocateHandle(table, type);
            if (handle is not null)
            {
                void** extraInfoSlot = HasExtraInfo(type) ? FetchExtraInfoSlot(handle, type) : null;
                if (HasExtraInfo(type) && extraInfoSlot is null)
                {
                    FreeSlot(handle, type);
                    table->Lock.Leave();
                    return null;
                }

                table->HandleCount++;
                if (extraInfoSlot is not null)
                {
                    *extraInfoSlot = extraInfo;
                }

                StoreObject(handle, value);
            }

            table->Lock.Leave();
            return handle;
        }

        private static OBJECTHANDLE__* AllocateHandle(HandleTable* table, HandleType type)
        {
            HandleTableSegment* segment = table->SegmentList;
            while (segment is not null)
            {
                for (int block = 0; block < HandleBlocksPerSegment; block++)
                {
                    if (segment->BlockType[block] == (byte)type)
                    {
                        OBJECTHANDLE__* handle = AllocateFromBlock(segment, block);
                        if (handle is not null)
                        {
                            return handle;
                        }
                    }
                }

                segment = segment->NextSegment;
            }

            segment = table->SegmentList;
            while (segment is not null)
            {
                OBJECTHANDLE__* handle = AllocateFromFreeBlock(segment, type);
                if (handle is not null)
                {
                    return handle;
                }

                segment = segment->NextSegment;
            }

            segment = AllocateSegment(table);
            if (segment is null)
            {
                return null;
            }

            OBJECTHANDLE__* newHandle = AllocateFromFreeBlock(segment, type);
            if (newHandle is null)
            {
                GCToOSInterface.VirtualRelease(segment, HandleSegmentSize);
                return null;
            }

            segment->NextSegment = table->SegmentList;
            table->SegmentList = segment;

            return newHandle;
        }

        private static OBJECTHANDLE__* AllocateFromFreeBlock(HandleTableSegment* segment, HandleType type)
        {
            for (int handleBlock = 0; handleBlock < HandleBlocksPerSegment; handleBlock++)
            {
                if (segment->BlockType[handleBlock] != TypeInvalid)
                {
                    continue;
                }

                int dataBlock = -1;
                if (HasExtraInfo(type))
                {
                    dataBlock = FindFreeBlock(segment, handleBlock);
                    if (dataBlock < 0 || !InitializeBlock(segment, dataBlock, InternalDataBlockType))
                    {
                        continue;
                    }
                }

                if (!InitializeBlock(segment, handleBlock, (byte)type))
                {
                    if (dataBlock >= 0)
                    {
                        FreeHandleBlock(segment, dataBlock);
                    }

                    continue;
                }

                if (dataBlock >= 0)
                {
                    segment->UserData[handleBlock] = (byte)dataBlock;
                }

                OBJECTHANDLE__* handle = AllocateFromBlock(segment, handleBlock);
                if (handle is not null)
                {
                    return handle;
                }

                FreeHandleBlock(segment, handleBlock);
                if (dataBlock >= 0)
                {
                    FreeHandleBlock(segment, dataBlock);
                }
            }

            return null;
        }

        private static OBJECTHANDLE__* AllocateFromBlock(HandleTableSegment* segment, int block)
        {
            if (!EnsureBlockCommitted(segment, block))
            {
                return null;
            }

            uint mask = segment->FreeMask[block * 2];
            if (mask != 0)
            {
                int bit = BitOperations.TrailingZeroCount(mask);
                segment->FreeMask[block * 2] = mask & ~(1u << bit);
                return SlotAddress(segment, block, bit);
            }

            mask = segment->FreeMask[(block * 2) + 1];
            if (mask == 0)
            {
                return null;
            }

            int upperBit = BitOperations.TrailingZeroCount(mask);
            segment->FreeMask[(block * 2) + 1] = mask & ~(1u << upperBit);
            return SlotAddress(segment, block, upperBit + 32);
        }

        private static bool InitializeBlock(HandleTableSegment* segment, int block, byte type)
        {
            if (!EnsureBlockCommitted(segment, block))
            {
                return false;
            }

            segment->UserData[block] = BlockInvalid;
            segment->FreeMask[block * 2] = uint.MaxValue;
            segment->FreeMask[(block * 2) + 1] = uint.MaxValue;
            segment->BlockType[block] = type;
            return true;
        }

        private static void FreeHandleBlock(HandleTableSegment* segment, int block)
        {
            segment->BlockType[block] = TypeInvalid;
            segment->UserData[block] = BlockInvalid;
            segment->FreeMask[block * 2] = uint.MaxValue;
            segment->FreeMask[(block * 2) + 1] = uint.MaxValue;
        }

        private static int FindFreeBlock(HandleTableSegment* segment, int excludedBlock)
        {
            for (int block = 0; block < HandleBlocksPerSegment; block++)
            {
                if (block != excludedBlock && segment->BlockType[block] == TypeInvalid)
                {
                    return block;
                }
            }

            return -1;
        }

        private static HandleTableSegment* AllocateSegment(HandleTable* table)
        {
            void* memory = GCToOSInterface.VirtualReserve(HandleSegmentSize, HandleSegmentSize, 0);
            if (memory is null || !GCToOSInterface.VirtualCommit(memory, HandleHeaderSize))
            {
                if (memory is not null)
                {
                    GCToOSInterface.VirtualRelease(memory, HandleSegmentSize);
                }

                return null;
            }

            NativeMemoryClear(memory, HandleHeaderSize);
            HandleTableSegment* segment = (HandleTableSegment*)memory;
            segment->OwnerTable = table;
            segment->NextSegment = null;
            segment->FreeList = 0;
            segment->EmptyLine = 0;
            segment->CommitLine = 0;
            segment->DecommitLine = 0;
            segment->Sequence = 0;
            for (int block = 0; block < HandleBlocksPerSegment; block++)
            {
                int generation = block * sizeof(uint);
                segment->Generation[generation] = byte.MaxValue;
                segment->Generation[generation + 1] = byte.MaxValue;
                segment->Generation[generation + 2] = byte.MaxValue;
                segment->Generation[generation + 3] = byte.MaxValue;
                segment->Allocation[block] = (byte)(block + 1 < HandleBlocksPerSegment ? block + 1 : BlockInvalid);
                segment->FreeMask[block * 2] = uint.MaxValue;
                segment->FreeMask[(block * 2) + 1] = uint.MaxValue;
                segment->BlockType[block] = TypeInvalid;
                segment->UserData[block] = BlockInvalid;
            }

            for (int type = 0; type < HandleMaxInternalTypes; type++)
            {
                segment->Tail[type] = BlockInvalid;
                segment->Hint[type] = BlockInvalid;
            }

            return segment;
        }

        private static bool EnsureBlockCommitted(HandleTableSegment* segment, int block)
        {
            if ((uint)block >= HandleBlocksPerSegment)
            {
                return false;
            }

            int commitLine = segment->CommitLine;
            if (block < commitLine)
            {
                return true;
            }

            nuint pageSize = GCToOSInterface.GetPageSize();
            if (pageSize < (nuint)HandleBlockSize || pageSize % (nuint)HandleBlockSize != 0)
            {
                return false;
            }

            int blocksPerPage = (int)(pageSize / (nuint)HandleBlockSize);
            while (block >= commitLine)
            {
                nuint commitOffset = (nuint)HandleHeaderSize + ((nuint)commitLine * (nuint)HandleBlockSize);
                if (commitOffset + pageSize > (nuint)HandleSegmentSize
                    || !GCToOSInterface.VirtualCommit((byte*)segment + commitOffset, pageSize))
                {
                    return false;
                }

                segment->DecommitLine = (byte)commitLine;
                commitLine += blocksPerPage;
                segment->CommitLine = (byte)commitLine;
            }

            return true;
        }

        private static bool EnsureHandleCommitted(OBJECTHANDLE__* handle)
        {
            HandleTableSegment* segment = GetSegment(handle);
            if (segment is null)
            {
                return false;
            }

            nuint offset = (nuint)((byte*)handle - ((byte*)segment + HandleHeaderSize));
            int block = (int)(offset / (nuint)HandleBlockSize);
            return EnsureBlockCommitted(segment, block);
        }

        private static OBJECTHANDLE__* SlotAddress(HandleTableSegment* segment, int block, int slot)
        {
            byte* address = (byte*)segment + HandleHeaderSize + (block * HandleBlockSize) + (slot * HandleSlotSize);
            return (OBJECTHANDLE__*)address;
        }

        private static HandleTable* GetHandleTable(OBJECTHANDLE__* handle)
        {
            if (handle is null)
            {
                return null;
            }

            HandleTableSegment* segment = (HandleTableSegment*)((nuint)handle & ~(nuint)(HandleSegmentSize - 1));
            return segment->OwnerTable;
        }

        private static HandleTableSegment* GetSegment(OBJECTHANDLE__* handle)
        {
            return handle is null
                ? null
                : (HandleTableSegment*)((nuint)handle & ~(nuint)(HandleSegmentSize - 1));
        }

        private static HandleType FetchType(OBJECTHANDLE__* handle)
        {
            HandleTableSegment* segment = GetSegment(handle);
            if (segment is null)
            {
                return HandleType.HNDTYPE_STRONG;
            }

            nuint offset = (nuint)((byte*)handle - ((byte*)segment + HandleHeaderSize));
            int block = (int)(offset / (nuint)HandleBlockSize);
            return (HandleType)segment->BlockType[block];
        }

        private static Object* FetchObject(OBJECTHANDLE__* handle)
        {
            if (handle is null)
            {
                return null;
            }

            if (!EnsureHandleCommitted(handle))
            {
                FailFast();
                return null;
            }

            return (Object*)*(nint*)handle;
        }

        private static void StoreObject(OBJECTHANDLE__* handle, Object* value)
        {
            if (handle is not null)
            {
                if (!EnsureHandleCommitted(handle))
                {
                    FailFast();
                    return;
                }

                Interlocked.ExchangePointer((nint*)handle, value);
            }
        }

        private static void DestroyHandle(OBJECTHANDLE__* handle, HandleType type)
        {
            if (handle is null)
            {
                return;
            }

            HandleTable* table = GetHandleTable(handle);
            if (table is null)
            {
                return;
            }

            table->Lock.Enter();
            StoreObject(handle, null);
            if (!ClearExtraInfo(handle, type))
            {
                FailFast();
                table->Lock.Leave();
                return;
            }

            FreeSlot(handle, type);
            if (table->HandleCount != 0)
            {
                table->HandleCount--;
            }
            table->Lock.Leave();
        }

        private static void FreeSlot(OBJECTHANDLE__* handle, HandleType type)
        {
            HandleTableSegment* segment = GetSegment(handle);
            if (segment is null)
            {
                return;
            }

            nuint offset = (nuint)((byte*)handle - ((byte*)segment + HandleHeaderSize));
            int block = (int)(offset / (nuint)HandleBlockSize);
            int slot = (int)((offset % (nuint)HandleBlockSize) / (nuint)HandleSlotSize);
            if (segment->BlockType[block] != (byte)type)
            {
                return;
            }

            if (slot < 32)
            {
                segment->FreeMask[block * 2] |= 1u << slot;
            }
            else
            {
                segment->FreeMask[(block * 2) + 1] |= 1u << (slot - 32);
            }
        }

        private static bool ClearExtraInfo(OBJECTHANDLE__* handle, HandleType type)
        {
            HandleTableSegment* segment = GetSegment(handle);
            if (segment is null)
            {
                return true;
            }

            nuint offset = (nuint)((byte*)handle - ((byte*)segment + HandleHeaderSize));
            int block = (int)(offset / (nuint)HandleBlockSize);
            int slot = (int)((offset % (nuint)HandleBlockSize) / (nuint)HandleSlotSize);
            if (segment->BlockType[block] != (byte)type)
            {
                return true;
            }

            byte dataBlock = segment->UserData[block];
            if (dataBlock == BlockInvalid)
            {
                return true;
            }

            if (!EnsureBlockCommitted(segment, dataBlock))
            {
                return false;
            }

            *(void**)SlotAddress(segment, dataBlock, slot) = null;
            return true;
        }

        private static void SetDependentHandleSecondaryCore(OBJECTHANDLE__* handle, Object* value)
        {
            void** slot = FetchExtraInfoSlot(handle, HandleType.HNDTYPE_DEPENDENT);
            if (slot is not null)
            {
                Interlocked.ExchangePointer((nint*)slot, value);
            }
        }

        private static void** FetchExtraInfoSlot(OBJECTHANDLE__* handle, HandleType type)
        {
            HandleTableSegment* segment = GetSegment(handle);
            if (segment is null)
            {
                return null;
            }

            nuint offset = (nuint)((byte*)handle - ((byte*)segment + HandleHeaderSize));
            int block = (int)(offset / (nuint)HandleBlockSize);
            int slot = (int)((offset % (nuint)HandleBlockSize) / (nuint)HandleSlotSize);
            if (segment->BlockType[block] != (byte)type)
            {
                return null;
            }

            byte dataBlock = segment->UserData[block];
            if (dataBlock == BlockInvalid)
            {
                return null;
            }

            if (!EnsureBlockCommitted(segment, dataBlock))
            {
                return null;
            }

            return (void**)SlotAddress(segment, dataBlock, slot);
        }

        private static bool HasExtraInfo(HandleType type)
        {
            return type is HandleType.HNDTYPE_VARIABLE
                or HandleType.HNDTYPE_DEPENDENT
                or HandleType.HNDTYPE_SIZEDREF
                or HandleType.HNDTYPE_WEAK_NATIVE_COM
                or HandleType.HNDTYPE_WEAK_INTERIOR_POINTER
                or HandleType.HNDTYPE_CROSSREFERENCE;
        }

        private static void DestroyBucket(HandleTableBucket* bucket)
        {
            if (bucket->Tables is null)
            {
                return;
            }

            for (uint i = 0; i < HandleTableCount; i++)
            {
                if (bucket->Tables[i] is not null)
                {
                    DestroyHandleTable(bucket->Tables[i]);
                }
            }

            GCUnixSyncImports.free(bucket->Tables);
            bucket->Tables = null;
        }

        private static HandleTypeCache* GetMainCache(HandleTable* table)
        {
            return (HandleTypeCache*)((byte*)table + sizeof(HandleTable));
        }

        private static HandleTable* CreateHandleTable()
        {
            nuint size = (nuint)sizeof(HandleTable) + ((nuint)HandleMaxInternalTypes * (nuint)sizeof(HandleTypeCache));
            HandleTable* table = (HandleTable*)GCUnixSyncImports.malloc(size);
            if (table is null)
            {
                return null;
            }

            NativeMemoryClear(table, size);
            table->SegmentList = null;
            table->TypeCount = HandleMaxPublicTypes;
            table->TableIndex = uint.MaxValue;
            for (int i = 0; i < HandleMaxInternalTypes; i++)
            {
                table->TypeFlags[i] = HasExtraInfo((HandleType)i) ? 1u : 0u;
            }

            table->Lock.Init(0);
            HandleTypeCache* mainCache = GetMainCache(table);
            for (int i = 0; i < HandleMaxInternalTypes; i++)
            {
                mainCache[i].FreeIndex = HandlesPerCacheBank;
            }

            table->SegmentList = AllocateSegment(table);
            if (table->SegmentList is null)
            {
                table->Lock.Destroy();
                GCUnixSyncImports.free(table);
                return null;
            }

            return table;
        }

        private static void DestroyHandleTable(HandleTable* table)
        {
            if (table is null)
            {
                return;
            }

            table->Lock.Destroy();
            HandleTableSegment* segment = table->SegmentList;
            while (segment is not null)
            {
                HandleTableSegment* next = segment->NextSegment;
                GCToOSInterface.VirtualRelease(segment, HandleSegmentSize);
                segment = next;
            }

            GCUnixSyncImports.free(table);
        }

        private static void NativeMemoryClear(void* memory, nuint size)
        {
            byte* bytes = (byte*)memory;
            for (nuint i = 0; i < size; i++)
            {
                bytes[i] = 0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FailFast()
        {
            System.Runtime.InternalCalls.RhpFallbackFailFast();
        }
    }
}
