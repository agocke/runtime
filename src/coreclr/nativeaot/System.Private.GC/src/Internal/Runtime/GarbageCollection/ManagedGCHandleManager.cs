// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The managed implementation of <c>gcinterface.h</c>'s <c>IGCHandleManager</c> and
    /// <c>IGCHandleStore</c>, over the translated handle table.
    /// </summary>
    internal static unsafe class ManagedGCHandleManager
    {
        private static IGCHandleManagerVtable s_managerVtable;
        private static nint s_managerVtablePtr;

        private static IGCHandleStoreVtable s_storeVtable;
        private static nint s_storeVtablePtr;

        /// <summary>Builds the vtables and returns the <c>IGCHandleManager*</c> to hand to the EE.</summary>
        public static void* Create()
        {
            void** managerSlots = (void**)Unsafe.AsPointer(ref s_managerVtable);
            for (int i = 0; i < IGCHandleManagerVtable.SlotCount; i++)
            {
                managerSlots[i] = (void*)(delegate*<void>)&Unsupported;
            }

            s_managerVtable.Initialize = &Initialize;
            s_managerVtable.Shutdown = &Shutdown;
            s_managerVtable.GetGlobalHandleStore = &GetGlobalHandleStore;
            s_managerVtable.CreateHandleStore = &CreateHandleStore;
            s_managerVtable.DestroyHandleStore = &DestroyHandleStore;
            s_managerVtable.CreateGlobalHandleOfType = &CreateGlobalHandleOfType;
            s_managerVtable.CreateDuplicateHandle = &CreateDuplicateHandle;
            s_managerVtable.DestroyHandleOfType = &DestroyHandleOfType;
            s_managerVtable.DestroyHandleOfUnknownType = &DestroyHandleOfUnknownType;
            s_managerVtable.SetExtraInfoForHandle = &SetExtraInfoForHandle;
            s_managerVtable.GetExtraInfoFromHandle = &GetExtraInfoFromHandle;
            s_managerVtable.StoreObjectInHandle = &StoreObjectInHandle;
            s_managerVtable.StoreObjectInHandleIfNull = &StoreObjectInHandleIfNull;
            s_managerVtable.SetDependentHandleSecondary = &SetDependentHandleSecondary;
            s_managerVtable.GetDependentHandleSecondary = &GetDependentHandleSecondary;
            s_managerVtable.InterlockedCompareExchangeObjectInHandle = &InterlockedCompareExchangeObjectInHandle;
            s_managerVtable.HandleFetchType = &HandleFetchType;
            s_managerVtable.TraceRefCountedHandles = &TraceRefCountedHandles;

            void** storeSlots = (void**)Unsafe.AsPointer(ref s_storeVtable);
            for (int i = 0; i < IGCHandleStoreVtable.SlotCount; i++)
            {
                storeSlots[i] = (void*)(delegate*<void>)&Unsupported;
            }

            s_storeVtable.Uproot = &Uproot;
            s_storeVtable.ContainsHandle = &ContainsHandle;
            s_storeVtable.CreateHandleOfType = &CreateHandleOfType;
            s_storeVtable.CreateHandleOfType_2 = &CreateHandleOfType_2;
            s_storeVtable.CreateHandleWithExtraInfo = &CreateHandleWithExtraInfo;
            s_storeVtable.CreateDependentHandle = &CreateDependentHandle;

            s_managerVtablePtr = (nint)Unsafe.AsPointer(ref s_managerVtable);
            s_storeVtablePtr = (nint)Unsafe.AsPointer(ref s_storeVtable);
            return Unsafe.AsPointer(ref s_managerVtablePtr);
        }

        [RuntimeImport("*", "ManagedGC_Unsupported")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void FailFastUnsupported();

        private static void Unsupported() => FailFastUnsupported();

        // ------------------------------------------------------------------------------------
        // Handle management
        // ------------------------------------------------------------------------------------

        private static void DestroyHandle(OBJECTHANDLE handle, uint type)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            HandleTableManager.HndDestroyHandle(
                HandleTableManager.HndGetHandleTable(handle),
                type,
                handle);
            criticalRegion.Exit();
        }

        private static void DestroyHandleOfUnknownTypeCore(OBJECTHANDLE handle)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            HandleTableManager.HndDestroyHandleOfUnknownType(
                HandleTableManager.HndGetHandleTable(handle),
                handle);
            criticalRegion.Exit();
        }

        private static OBJECTHANDLE CreateHandle(byte* obj, HandleType type, void* extraInfo)
        {
            return CreateHandleInTable(
                GetTable(),
                obj,
                type,
                extraInfo);
        }

        // ------------------------------------------------------------------------------------
        // IGCHandleManager
        // ------------------------------------------------------------------------------------

        private static byte Initialize(void* thisPtr)
        {
            return ObjectHandle.Ref_Initialize() ? (byte)1 : (byte)0;
        }

        private static void Shutdown(void* thisPtr)
        {
            DestroyHandleStore(thisPtr, Unsafe.AsPointer(ref s_storeVtablePtr));
            ObjectHandle.Ref_Shutdown();
        }

        private static void* GetGlobalHandleStore(void* thisPtr) => Unsafe.AsPointer(ref s_storeVtablePtr);

        private static void* CreateHandleStore(void* thisPtr)
        {
            // Dead ABI slot: NativeAOT only uses the global store.
            Debug.Assert(false);
            return null;
        }

        private static void DestroyHandleStore(void* thisPtr, void* store)
        {
            _ = thisPtr;
            if (store == Unsafe.AsPointer(ref s_storeVtablePtr))
            {
                HandleTableBucket* bucket = GetGlobalBucket();
                if (bucket->pTable is not null)
                {
                    ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
                }
            }
        }

        private static OBJECTHANDLE CreateGlobalHandleOfType(void* thisPtr, byte* obj, HandleType type) =>
            CreateHandleInTable(
                ObjectHandle.g_HandleTableMap.pBuckets[0]->pTable[ObjectHandle.GetCurrentThreadHomeHeapNumber()],
                obj,
                type,
                null);

        private static OBJECTHANDLE CreateDuplicateHandle(void* thisPtr, OBJECTHANDLE handle)
        {
            return CreateHandleInTable(
                HandleTableManager.HndGetHandleTable(handle),
                (byte*)GCEnv.VolatileLoad((nuint*)handle.Value),
                HandleType.HNDTYPE_DEFAULT,
                null);
        }

        private static void DestroyHandleOfType(void* thisPtr, OBJECTHANDLE handle, HandleType type) =>
            DestroyHandle(handle, (uint)type);

        private static void DestroyHandleOfUnknownType(void* thisPtr, OBJECTHANDLE handle) =>
            DestroyHandleOfUnknownTypeCore(handle);

        private static void SetExtraInfoForHandle(void* thisPtr, OBJECTHANDLE handle, HandleType type, void* pExtraInfo) =>
            HandleTableManager.HndSetHandleExtraInfo(handle, (uint)type, (nuint)pExtraInfo);

        private static void* GetExtraInfoFromHandle(void* thisPtr, OBJECTHANDLE handle) =>
            (void*)HandleTableManager.HndGetHandleExtraInfo(handle);

        private static void StoreObjectInHandle(void* thisPtr, OBJECTHANDLE handle, byte* obj) =>
            HandleTableManager.HndAssignHandle(handle, obj);

        private static byte StoreObjectInHandleIfNull(void* thisPtr, OBJECTHANDLE handle, byte* obj) =>
            HandleTableManager.HndFirstAssignHandle(handle, obj);

        private static void SetDependentHandleSecondary(void* thisPtr, OBJECTHANDLE handle, byte* obj) =>
            HandleTableManager.SetDependentHandleSecondary(handle, obj);

        private static byte* GetDependentHandleSecondary(void* thisPtr, OBJECTHANDLE handle) =>
            HandleTableManager.GetDependentHandleSecondary(handle);

        private static byte* InterlockedCompareExchangeObjectInHandle(void* thisPtr, OBJECTHANDLE handle, byte* obj, byte* comparandObject) =>
            HandleTableManager.HndInterlockedCompareExchangeHandle(handle, obj, comparandObject);

        private static HandleType HandleFetchType(void* thisPtr, OBJECTHANDLE handle) =>
            (HandleType)HandleTableCore.HandleFetchType(handle);

        private static void TraceRefCountedHandles(void* thisPtr, delegate* unmanaged<byte**, nuint*, nuint, nuint, void> callback, nuint lp1, nuint lp2)
        {
#if MULTIPLE_HEAPS
            _ = thisPtr;
            _ = callback;
            _ = lp1;
            _ = lp2;
            Unsupported();
#else
            RefCountedTraceInfo info = new()
            {
                callback = callback,
                param1 = lp1,
                param2 = lp2,
            };
            HandleTableScan.Ref_TraceRefCountHandles(
                &TraceRefCountedHandle,
                (nuint)(void*)&info,
                0);
#endif
        }

        private struct RefCountedTraceInfo
        {
            public delegate* unmanaged<byte**, nuint*, nuint, nuint, void> callback;
            public nuint param1;
            public nuint param2;
        }

        private static void TraceRefCountedHandle(
            byte** obj,
            nuint* extraInfo,
            nuint param1,
            nuint param2)
        {
            _ = param2;
            RefCountedTraceInfo* info = (RefCountedTraceInfo*)param1;
            info->callback(
                obj,
                extraInfo,
                info->param1,
                info->param2);
        }

        // ------------------------------------------------------------------------------------
        // IGCHandleStore
        // ------------------------------------------------------------------------------------

        private static void Uproot(void* thisPtr)
        {
            // Dead ABI slot retained by the native WKS implementation.
            Debug.Assert(false);
        }

        private static byte ContainsHandle(void* thisPtr, OBJECTHANDLE handle)
        {
            // Dead ABI slot retained by the native WKS implementation.
            Debug.Assert(false);
            return 0;
        }

        private static OBJECTHANDLE CreateHandleOfType(void* thisPtr, byte* obj, HandleType type) =>
            CreateHandle(obj, type, null);

        private static OBJECTHANDLE CreateHandleOfType_2(void* thisPtr, byte* obj, HandleType type, int heapToAffinitizeTo) =>
            CreateHandleInTable(
                GetGlobalBucket()->pTable[heapToAffinitizeTo],
                obj,
                type,
                null);

        private static OBJECTHANDLE CreateHandleWithExtraInfo(void* thisPtr, byte* obj, HandleType type, void* pExtraInfo) =>
            CreateHandleInTable(
                GetGlobalBucket()->pTable[ObjectHandle.GetCurrentThreadHomeHeapNumber()],
                obj,
                type,
                pExtraInfo);

        private static OBJECTHANDLE CreateDependentHandle(void* thisPtr, byte* primary, byte* secondary) =>
            CreateDependentHandleCore(primary, secondary);

        private static OBJECTHANDLE CreateDependentHandleCore(byte* primary, byte* secondary)
        {
            OBJECTHANDLE handle = CreateHandleInTable(
                GetGlobalBucket()->pTable[ObjectHandle.GetCurrentThreadHomeHeapNumber()],
                primary,
                HandleType.HNDTYPE_DEPENDENT,
                null);
            if (!handle.IsNull)
            {
                HandleTableManager.SetDependentHandleSecondary(handle, secondary);
            }

            return handle;
        }

        private static HandleTableBucket* GetGlobalBucket()
        {
            return (HandleTableBucket*)Unsafe.AsPointer(ref ObjectHandle.g_GlobalHandleTableBucket);
        }

        private static HandleTable* GetTable()
        {
            return GetGlobalBucket()->pTable[0];
        }

        private static OBJECTHANDLE CreateHandleInTable(
            HandleTable* table,
            byte* obj,
            HandleType type,
            void* extraInfo)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            OBJECTHANDLE result = HandleTableManager.HndCreateHandle(
                table,
                (uint)type,
                obj,
                (nuint)extraInfo);
            criticalRegion.Exit();
            return result;
        }
    }
}
