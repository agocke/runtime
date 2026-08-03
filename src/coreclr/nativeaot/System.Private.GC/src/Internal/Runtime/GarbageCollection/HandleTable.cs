// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the handle-table lifecycle entrypoints of src/coreclr/gc/handletable.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class HandleTableManager
    {
        public static HandleTable* HndCreateHandleTable(uint* pTypeFlags, uint uTypeCount)
        {
            Debug.Assert(uTypeCount != 0);
            Debug.Assert(uTypeCount <= HandleTableConstants.HANDLE_MAX_PUBLIC_TYPES);
            Debug.Assert(sizeof(_TableSegmentHeader) <= HandleTableConstants.HANDLE_HEADER_SIZE);

            uint dwSize = (uint)sizeof(HandleTable) + (uTypeCount * (uint)sizeof(HandleTypeCache));
            HandleTable* pTable = (HandleTable*)SyncImports.ManagedGC_AllocZeroed(dwSize);
            if (pTable == null)
            {
                return null;
            }

            pTable->pSegmentList = HandleTableCore.SegmentAlloc(pTable);
            if (pTable->pSegmentList == null)
            {
                SyncImports.ManagedGC_Free(pTable);
                return null;
            }

            if (!pTable->Lock.InitNoThrow(CrstType.CrstHandleTable, CrstFlags.CRST_DEFAULT))
            {
                HandleTableCore.SegmentFree(pTable->pSegmentList);
                SyncImports.ManagedGC_Free(pTable);
                return null;
            }

            pTable->uTypeCount = uTypeCount;
            pTable->uTableIndex = uint.MaxValue;

            uint u;
            for (u = 0; u < uTypeCount; u++)
            {
                pTable->rgTypeFlags[u] = pTypeFlags[u];
            }

            while (u < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES)
            {
                pTable->rgTypeFlags[u++] = HandleTableConstants.HNDF_NORMAL;
            }

            HandleTypeCache* pMainCache = GetMainCache(pTable);
            for (u = 0; u < uTypeCount; u++)
            {
                pMainCache[u].lFreeIndex = HandleTableConstants.HANDLES_PER_CACHE_BANK;
            }

#if DEBUG
            pTable->_DEBUG_iMaxGen = -1;
#endif

            return pTable;
        }

        public static void HndDestroyHandleTable(HandleTable* pTable)
        {
            pTable->Lock.Destroy();

            TableSegment* pSegment = pTable->pSegmentList;
            pTable->pSegmentList = null;

            while (pSegment != null)
            {
                TableSegment* pNextSegment = pSegment->Header.pNextSegment;
                HandleTableCore.SegmentFree(pSegment);
                pSegment = pNextSegment;
            }

            SyncImports.ManagedGC_Free(pTable);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HandleTypeCache* GetMainCache(HandleTable* pTable)
        {
            return (HandleTypeCache*)((byte*)pTable + sizeof(HandleTable));
        }

        public static void HndSetHandleTableIndex(HandleTable* pTable, uint uTableIndex)
        {
            pTable->uTableIndex = uTableIndex;
        }

        public static uint HndGetHandleTableIndex(HandleTable* pTable)
        {
            Debug.Assert(pTable->uTableIndex != uint.MaxValue);

            return pTable->uTableIndex;
        }

        public static int GetConvertedGeneration(byte* obj)
        {
            uint generation = ManagedGCHeap.GenerationOf(obj);
            return generation == int.MaxValue ? (int)ManagedGCHeap.MaxGeneration : (int)generation;
        }

        private static void HndLogSetEvent(OBJECTHANDLE handle, byte* value)
        {
            if (GCEvents.GCEventEnabledSetGCHandle() || GCEvents.GCEventEnabledPrvSetGCHandle())
            {
                uint hndType = HandleTableCore.HandleFetchType(handle);
                uint generation = value != null ? ManagedGCHeap.GenerationOf(value) : 0;
                GCEvents.GCEventFireSetGCHandle(handle.Value, value, hndType, generation);
                GCEvents.GCEventFirePrvSetGCHandle(handle.Value, value, hndType, generation);

                if (hndType == (uint)HandleType.HNDTYPE_ASYNCPINNED)
                {
                    // The EE invokes this while already in cooperative mode, so use the plain
                    // managed entrypoint rather than a reverse-P/Invoke thunk.
                    delegate*<byte*, byte*, void*, void> callback = &HndLogSetEventAsyncPinned;
                    GCToEEInterface.WalkAsyncPinned(
                        value,
                        value,
                        (delegate* unmanaged<byte*, byte*, void*, void>)callback);
                }
            }
        }

        private static void HndLogSetEventAsyncPinned(byte* from, byte* to, void* context)
        {
            byte* overlapped = (byte*)context;
            uint generation = to != null ? ManagedGCHeap.GenerationOf(to) : 0;
            GCEvents.GCEventFireSetGCHandle(
                overlapped,
                to,
                (uint)HandleType.HNDTYPE_PINNED,
                generation);
        }

        public static void HndWriteBarrierWorker(OBJECTHANDLE handle, byte* value)
        {
            Debug.Assert(value != null);

            byte* barrier = (byte*)((nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_ALIGN_MASK);
            Debug.Assert(barrier != null);

            nuint offset = (nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK;
            Debug.Assert(offset >= HandleTableConstants.HANDLE_HEADER_SIZE);

            offset = (offset - HandleTableConstants.HANDLE_HEADER_SIZE)
                / (HandleTableConstants.HANDLE_SIZE * HandleTableConstants.HANDLE_HANDLES_PER_CLUMP);

            byte* pClumpAge = barrier + offset;
            if (GCEnv.VolatileLoad(pClumpAge) != 0)
            {
                int generation = GetConvertedGeneration(value);
                uint uType = HandleTableCore.HandleFetchType(handle);

                if (uType == (uint)HandleType.HNDTYPE_ASYNCPINNED)
                {
                    generation = 0;
                }

                if (uType == (uint)HandleType.HNDTYPE_DEPENDENT)
                {
                    generation = 0;
                }

                if (GCEnv.VolatileLoad(pClumpAge) > (byte)generation)
                {
                    GCEnv.VolatileStore(pClumpAge, 0);
                }
            }
        }

        public static void HndAssignHandle(OBJECTHANDLE handle, byte* obj)
        {
            Debug.Assert(!handle.IsNull);

            HndLogSetEvent(handle, obj);

            if (obj != null)
            {
                HndWriteBarrierWorker(handle, obj);
            }

            GCEnv.VolatileStore((nuint*)handle.Value, (nuint)obj);
        }

        public static void HndAssignHandleGC(OBJECTHANDLE handle, byte* obj)
        {
            Debug.Assert(!handle.IsNull);

            if (obj != null)
            {
                HndWriteBarrierWorker(handle, obj);
            }

            GCEnv.VolatileStore((nuint*)handle.Value, (nuint)obj);
        }

        public static byte* HndInterlockedCompareExchangeHandle(
            OBJECTHANDLE handle,
            byte* obj,
            byte* oldObj)
        {
            Debug.Assert(!handle.IsNull);

            if (obj != null)
            {
                HndWriteBarrierWorker(handle, obj);
            }

            byte* result = (byte*)Interlocked.CompareExchangePointer(
                (void**)handle.Value,
                obj,
                oldObj);

            if (result == oldObj)
            {
                HndLogSetEvent(handle, obj);
            }

            return result;
        }

        public static byte HndFirstAssignHandle(OBJECTHANDLE handle, byte* obj)
        {
            Debug.Assert(!handle.IsNull);

            byte success = Interlocked.CompareExchangePointer(
                (void**)handle.Value,
                obj,
                null) == null ? (byte)1 : (byte)0;

            if (success != 0)
            {
                if (obj != null)
                {
                    HndWriteBarrierWorker(handle, obj);
                }

                HndLogSetEvent(handle, obj);
            }

            return success;
        }

        public static void SetDependentHandleSecondary(OBJECTHANDLE handle, byte* obj)
        {
            Debug.Assert(!handle.IsNull);

            if (obj != null)
            {
                HndWriteBarrierWorker(handle, obj);
            }

            HndSetHandleExtraInfo(
                handle,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                (nuint)obj);
        }

        public static byte* GetDependentHandleSecondary(OBJECTHANDLE handle)
        {
            return (byte*)HndGetHandleExtraInfo(handle);
        }

        public static OBJECTHANDLE HndCreateHandle(HandleTable* pTable, uint uType, byte* obj, nuint lExtraInfo)
        {
            Debug.Assert(uType < pTable->uTypeCount);

            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(pTable, uType);
            if (handle.IsNull)
            {
                return default;
            }

#if DEBUG
            if (*(nuint*)handle.Value == HandleTableConstants.DEBUG_DestroyedHandleValue)
            {
                *(nuint*)handle.Value = 0;
            }
#endif

            Debug.Assert(*(nuint*)handle.Value == 0);

            if (lExtraInfo != 0)
            {
                HandleTableCore.HandleQuickSetUserData(handle, lExtraInfo);
            }

            HndAssignHandle(handle, obj);

            return handle;
        }

        public static void HndDestroyHandle(HandleTable* pTable, uint uType, OBJECTHANDLE handle)
        {
            GCEvents.GCEventFireDestroyGCHandle(handle.Value);
            GCEvents.GCEventFirePrvDestroyGCHandle(handle.Value);

            Debug.Assert(!handle.IsNull);
            Debug.Assert(uType < pTable->uTypeCount);
            Debug.Assert(HandleTableCore.HandleFetchType(handle) == uType);

            HandleTableCache.TableFreeSingleHandleToCache(pTable, uType, handle);
        }

        public static void HndDestroyHandleOfUnknownType(HandleTable* pTable, OBJECTHANDLE handle)
        {
            Debug.Assert(!handle.IsNull);

            HndDestroyHandle(pTable, HandleTableCore.HandleFetchType(handle), handle);
        }

        public static void HndSetHandleExtraInfo(OBJECTHANDLE handle, uint uType, nuint lExtraInfo)
        {
            nuint* pUserData = HandleTableCore.HandleValidateAndFetchUserDataPointer(handle, uType);

            if (pUserData != null)
            {
                *pUserData = lExtraInfo;
            }
        }

        public static nuint HndCompareExchangeHandleExtraInfo(
            OBJECTHANDLE handle,
            uint uType,
            nuint lOldExtraInfo,
            nuint lNewExtraInfo)
        {
            nuint* pUserData = HandleTableCore.HandleValidateAndFetchUserDataPointer(handle, uType);

            if (pUserData != null)
            {
                return (nuint)Interlocked.CompareExchangePointer(
                    (void**)pUserData,
                    (void*)lNewExtraInfo,
                    (void*)lOldExtraInfo);
            }

            Debug.Assert(false);

            return 0;
        }

        public static nuint HndGetHandleExtraInfo(OBJECTHANDLE handle)
        {
            nuint lExtraInfo = 0;
            nuint* pUserData = HandleTableCore.HandleQuickFetchUserDataPointer(handle);

            if (pUserData != null)
            {
                lExtraInfo = *pUserData;
            }

            return lExtraInfo;
        }

        public static HandleTable* HndGetHandleTable(OBJECTHANDLE handle)
        {
            return HandleTableCore.HandleFetchHandleTable(handle);
        }

        public static uint HndCountHandles(HandleTable* pTable)
        {
            uint uCacheCount = 0;
            uint uCount = pTable->dwCount;

            HandleTypeCache* pCache = GetMainCache(pTable);
            HandleTypeCache* pCacheEnd = pCache + pTable->uTypeCount;
            for (; pCache != pCacheEnd; pCache++)
            {
                int lFreeIndex = pCache->lFreeIndex;
                int lReserveIndex = pCache->lReserveIndex;

                if (lFreeIndex < 0)
                {
                    lFreeIndex = 0;
                }

                if (lReserveIndex < 0)
                {
                    lReserveIndex = 0;
                }

                uint uHandleCount = (uint)lReserveIndex
                    + (HandleTableConstants.HANDLES_PER_CACHE_BANK - (uint)lFreeIndex);

                uCacheCount += uHandleCount;
            }

            OBJECTHANDLE* pQuickCache = (OBJECTHANDLE*)pTable->rgQuickCache;
            OBJECTHANDLE* pQuickCacheEnd = pQuickCache + HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES;
            for (; pQuickCache != pQuickCacheEnd; pQuickCache++)
            {
                if (!pQuickCache->IsNull)
                {
                    uCacheCount++;
                }
            }

            return uCount - uCacheCount;
        }

        public static void HndNotifyGcCycleComplete(HandleTable* pTable, uint condemned, uint maxgen)
        {
            _ = pTable;
            _ = condemned;
            _ = maxgen;
        }

        public static uint HndCountAllHandles(bool fUseLocks)
        {
            uint uCount = 0;
            int offset = 0;
            int nSlots = ObjectHandle.getNumberOfSlots();
            HandleTableMap* walk = (HandleTableMap*)Unsafe.AsPointer(ref ObjectHandle.g_HandleTableMap);

            while (walk != null)
            {
                int nextOffset = (int)walk->dwMaxIndex;
                int max = nextOffset - offset;
                HandleTableBucket** pBucket = walk->pBuckets;
                HandleTableBucket** pLastBucket = pBucket + max;

                for (; pBucket != pLastBucket; pBucket++)
                {
                    if (*pBucket != null)
                    {
                        HandleTable** pTable = (*pBucket)->pTable;
                        HandleTable** pLastTable = pTable + nSlots;

                        if (fUseLocks)
                        {
                            for (; pTable != pLastTable; pTable++)
                            {
                                using HandleTableCrstHolder holder = new HandleTableCrstHolder(&(*pTable)->Lock);
                                uCount += HndCountHandles(*pTable);
                            }
                        }
                        else
                        {
                            for (; pTable != pLastTable; pTable++)
                            {
                                uCount += HndCountHandles(*pTable);
                            }
                        }
                    }
                }

                offset = nextOffset;
                walk = walk->pNext;
            }

            return uCount;
        }
    }
}
