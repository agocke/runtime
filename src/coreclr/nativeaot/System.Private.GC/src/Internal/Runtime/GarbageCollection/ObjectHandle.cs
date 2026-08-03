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

        public static HandleTableMap g_HandleTableMap;
        public static HandleTableBucket g_GlobalHandleTableBucket;

        public static int getNumberOfSlots()
        {
            return 1;
        }

        public static bool Ref_Initialize()
        {
            Debug.Assert(g_HandleTableMap.pBuckets == null);

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

            pBuckets[0] = pBucket;
            g_HandleTableMap.pBuckets = pBuckets;
            g_HandleTableMap.dwMaxIndex = HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE;
            g_HandleTableMap.pNext = null;
            return true;
        }

        public static void Ref_Shutdown()
        {
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
