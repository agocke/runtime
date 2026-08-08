// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the dependency-closed initialization and bucket-management subset of
// src/coreclr/gc/objecthandle.h and objecthandle.cpp.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HandleTableMap
    {
        public HandleTableBucket** pBuckets;
        public HandleTableMap* pNext;
        public uint dwMaxIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HandleTableBucket
    {
        public HandleTable** pTable;
        public uint HandleTableIndex;
    }

    internal static unsafe class ObjectHandle
    {
        private const uint TypeCount = 12;

        public const uint VHT_WEAK_SHORT = 0x00000100;
        public const uint VHT_WEAK_LONG = 0x00000200;
        public const uint VHT_STRONG = 0x00000400;
        public const uint VHT_PINNED = 0x00000800;

        public static HandleTableMap g_HandleTableMap;
        public static HandleTableBucket g_GlobalHandleTableBucket;
        private static DhContext* g_pDependentHandleContexts;

        public static bool DependentHandleContextsInitialized => g_pDependentHandleContexts is not null;

        public static int getNumberOfSlots()
        {
            return 1;
        }

        public static bool Ref_Initialize()
        {
            Debug.Assert(g_HandleTableMap.pBuckets == null);
            Debug.Assert(g_pDependentHandleContexts == null);

            HandleTableBucket** pBuckets = (HandleTableBucket**)SyncImports.ManagedGC_AllocZeroed(
                HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE * (nuint)sizeof(HandleTableBucket*));
            if (pBuckets == null)
            {
                return false;
            }

            HandleTableBucket* pBucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref g_GlobalHandleTableBucket);
            pBucket->HandleTableIndex = 0;

            int nSlots = getNumberOfSlots();
            pBucket->pTable = (HandleTable**)SyncImports.ManagedGC_AllocZeroed(
                (nuint)nSlots * (nuint)sizeof(HandleTable*));
            if (pBucket->pTable == null)
            {
                SyncImports.ManagedGC_Free(pBuckets);
                return false;
            }

            uint* typeFlags = stackalloc uint[(int)TypeCount]
            {
                HandleTableConstants.HNDF_NORMAL,
                HandleTableConstants.HNDF_NORMAL,
                HandleTableConstants.HNDF_NORMAL,
                HandleTableConstants.HNDF_NORMAL,
                HandleTableConstants.HNDF_EXTRAINFO,
                HandleTableConstants.HNDF_NORMAL,
                HandleTableConstants.HNDF_EXTRAINFO,
                HandleTableConstants.HNDF_NORMAL,
                HandleTableConstants.HNDF_EXTRAINFO,
                HandleTableConstants.HNDF_EXTRAINFO,
                HandleTableConstants.HNDF_EXTRAINFO,
                HandleTableConstants.HNDF_EXTRAINFO,
            };

            for (int cpuIndex = 0; cpuIndex < nSlots; cpuIndex++)
            {
                pBucket->pTable[cpuIndex] = HandleTableManager.HndCreateHandleTable(typeFlags, TypeCount);
                if (pBucket->pTable[cpuIndex] == null)
                {
                    DestroyBucketTables(pBucket, nSlots);
                    SyncImports.ManagedGC_Free(pBuckets);
                    return false;
                }

                HandleTableManager.HndSetHandleTableIndex(pBucket->pTable[cpuIndex], 0);
            }

            DhContext* contexts = (DhContext*)SyncImports.ManagedGC_AllocZeroed(
                (nuint)nSlots * (nuint)sizeof(DhContext));
            if (contexts == null)
            {
                DestroyBucketTables(pBucket, nSlots);
                SyncImports.ManagedGC_Free(pBuckets);
                return false;
            }

            pBuckets[0] = pBucket;
            g_HandleTableMap.pBuckets = pBuckets;
            g_HandleTableMap.dwMaxIndex = HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE;
            g_HandleTableMap.pNext = null;
            g_pDependentHandleContexts = contexts;
            return true;
        }

        public static void Ref_Shutdown()
        {
            if (g_pDependentHandleContexts != null)
            {
                SyncImports.ManagedGC_Free(g_pDependentHandleContexts);
                g_pDependentHandleContexts = null;
            }

            if (g_HandleTableMap.pBuckets != null)
            {
                HandleTableMap* walk = (HandleTableMap*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                    ref g_HandleTableMap);
                while (walk != null)
                {
                    SyncImports.ManagedGC_Free(walk->pBuckets);
                    walk = walk->pNext;
                }

                g_HandleTableMap.pNext = null;
                g_HandleTableMap.dwMaxIndex = 0;
                g_HandleTableMap.pBuckets = null;
            }
        }

        public static void Ref_RemoveHandleTableBucket(HandleTableBucket* pBucket)
        {
            nuint index = pBucket->HandleTableIndex;
            HandleTableMap* walk = (HandleTableMap*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref g_HandleTableMap);
            nuint offset = 0;

            while (walk != null)
            {
                if (index < walk->dwMaxIndex && index >= offset)
                {
                    if (walk->pBuckets[index - offset] == pBucket)
                    {
                        walk->pBuckets[index - offset] = null;
                        return;
                    }
                }

                offset = walk->dwMaxIndex;
                walk = walk->pNext;
            }
        }

        public static void Ref_DestroyHandleTableBucket(HandleTableBucket* pBucket)
        {
            Ref_RemoveHandleTableBucket(pBucket);
            DestroyBucketTables(pBucket, getNumberOfSlots());
        }

        public static int GetCurrentThreadHomeHeapNumber()
        {
            return 0;
        }

        public static DhContext* Ref_GetDependentHandleContext(ScanContext* sc)
        {
            _ = sc;
            Debug.Assert(g_pDependentHandleContexts != null);
            return g_pDependentHandleContexts;
        }

        public static bool Contains(HandleTableBucket* pBucket, OBJECTHANDLE handle)
        {
            if (handle.IsNull)
            {
                return false;
            }

            HandleTable* table = HandleTableManager.HndGetHandleTable(handle);
            for (int cpuIndex = 0; cpuIndex < getNumberOfSlots(); cpuIndex++)
            {
                if (table == pBucket->pTable[cpuIndex])
                {
                    return true;
                }
            }

            return false;
        }

        public static bool Ref_HasHandlesOfType(uint type)
        {
            HandleTableMap* walk =
                (HandleTableMap*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                    ref g_HandleTableMap);
            while (walk is not null)
            {
                for (uint bucketIndex = 0;
                     bucketIndex < HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE;
                     bucketIndex++)
                {
                    HandleTableBucket* bucket = walk->pBuckets[bucketIndex];
                    if (bucket is null)
                    {
                        continue;
                    }

                    for (int slot = 0; slot < getNumberOfSlots(); slot++)
                    {
                        HandleTable* table = bucket->pTable[slot];
                        if (table is null)
                        {
                            continue;
                        }

                        using HandleTableCrstHolder holder =
                            new(&table->Lock);
                        for (TableSegment* segment = table->pSegmentList;
                             segment is not null;
                             segment = segment->Header.pNextSegment)
                        {
                            if (segment->Header.rgTail[type] !=
                                HandleTableConstants.BLOCK_INVALID)
                            {
                                return true;
                            }
                        }
                    }
                }

                walk = walk->pNext;
            }

            return false;
        }

        public static uint GetVariableHandleType(OBJECTHANDLE handle)
        {
            return (uint)HandleTableManager.HndGetHandleExtraInfo(handle);
        }

        public static void UpdateVariableHandleType(OBJECTHANDLE handle, uint type)
        {
            if (!IsValidVariableHandleType(type))
            {
                Debug.Assert(false);
                return;
            }

            HandleTableManager.HndSetHandleExtraInfo(
                handle,
                (uint)HandleType.HNDTYPE_VARIABLE,
                type);
        }

        public static uint CompareExchangeVariableHandleType(
            OBJECTHANDLE handle,
            uint oldType,
            uint newType)
        {
            Debug.Assert(IsValidVariableHandleType(oldType) && IsValidVariableHandleType(newType));

            return (uint)HandleTableManager.HndCompareExchangeHandleExtraInfo(
                handle,
                (uint)HandleType.HNDTYPE_VARIABLE,
                oldType,
                newType);
        }

        private static bool IsValidVariableHandleType(uint type)
        {
            return type is VHT_WEAK_SHORT or VHT_WEAK_LONG or VHT_STRONG or VHT_PINNED;
        }

        private static void DestroyBucketTables(HandleTableBucket* pBucket, int slots)
        {
            if (pBucket->pTable != null)
            {
                for (int slot = 0; slot < slots; slot++)
                {
                    if (pBucket->pTable[slot] != null)
                    {
                        HandleTableManager.HndDestroyHandleTable(pBucket->pTable[slot]);
                    }
                }

                SyncImports.ManagedGC_Free(pBucket->pTable);
                pBucket->pTable = null;
            }
        }
    }
}
