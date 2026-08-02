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
    }
}
