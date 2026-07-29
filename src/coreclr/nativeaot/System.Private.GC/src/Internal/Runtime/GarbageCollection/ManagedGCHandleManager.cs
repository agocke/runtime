// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The managed implementation of <c>gcinterface.h</c>'s <c>IGCHandleManager</c> and
    /// <c>IGCHandleStore</c>, over a flat table of handle slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C++ handle table (<c>handletable.cpp</c> and friends, plan step 5) is organized into
    /// per-type segments so that a collection can scan just the handle types it cares about.
    /// Nothing scans handles here, so the whole structure collapses to one array of slots with a
    /// free list, and the handle types are recorded only so that <c>HandleFetchType</c> can
    /// answer.
    /// </para>
    /// <para>
    /// A handle is the address of a slot's object field. That is the same contract the C++
    /// handle table has: the EE reads and writes handles by dereferencing them directly, so the
    /// object reference must be the first word of the slot.
    /// </para>
    /// <para>
    /// Weak handles are never cleared and dependent handles never drop their secondary, because
    /// no object ever dies.
    /// </para>
    /// </remarks>
    internal static unsafe class ManagedGCHandleManager
    {
        /// <summary>
        /// Number of handles the process can have outstanding. Destroyed handles are reused, so
        /// this is a high-water mark rather than a total.
        /// </summary>
        private const int MaxHandles = 256 * 1024;

        private struct HandleSlot
        {
            /// <summary>
            /// The object. Must stay first: a handle is the address of this field, and the EE
            /// reads and writes handles by dereferencing them. Held as nint rather than byte*
            /// because Volatile and Interlocked cannot be instantiated over a pointer type.
            /// </summary>
            public nint Object;

            /// <summary>A dependent handle's secondary, or a handle type's extra info.</summary>
            public nint ExtraInfo;

            public HandleType Type;

            /// <summary>Index of the next free slot plus one, or zero for the end of the list.</summary>
            public uint NextFree;
        }

        private static IGCHandleManagerVtable s_managerVtable;
        private static nint s_managerVtablePtr;

        private static IGCHandleStoreVtable s_storeVtable;
        private static nint s_storeVtablePtr;

        private static HandleSlot* s_slots;
        private static int s_slotsUsed;

        /// <summary>
        /// Head of the free list: the low 32 bits are the index of the first free slot plus one
        /// (zero when the list is empty), and the high 32 bits are a counter that makes the
        /// compare-exchange in <see cref="AllocateSlot"/> immune to ABA.
        /// </summary>
        private static long s_freeListHead;

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
        // Slot management
        // ------------------------------------------------------------------------------------

        private static HandleSlot* AllocateSlot()
        {
            while (true)
            {
                long head = Volatile.Read(ref s_freeListHead);
                uint index = (uint)head;
                if (index == 0)
                {
                    break;
                }

                HandleSlot* candidate = s_slots + (index - 1);

                // If another thread has already taken this slot and handed it out, this reads a
                // live handle's fields rather than a free-list link. That is a harmless read of
                // mapped memory: the counter in the high half will have moved, so the exchange
                // below fails and the value is discarded.
                long next = ((head >> 32) + 1) << 32 | candidate->NextFree;
                if (Interlocked.CompareExchange(ref s_freeListHead, next, head) == head)
                {
                    return candidate;
                }
            }

            // Claimed with a compare-exchange rather than an unconditional increment so that
            // the counter saturates at MaxHandles. Incrementing past it would eventually
            // overflow to a negative index and hand out a pointer below the table.
            while (true)
            {
                int used = Volatile.Read(ref s_slotsUsed);
                if (used == MaxHandles)
                {
                    return null;
                }

                if (Interlocked.CompareExchange(ref s_slotsUsed, used + 1, used) == used)
                {
                    return s_slots + used;
                }
            }
        }

        private static void FreeSlot(HandleSlot* slot)
        {
            slot->Object = 0;
            slot->ExtraInfo = 0;

            uint index = (uint)(slot - s_slots) + 1;
            while (true)
            {
                long head = Volatile.Read(ref s_freeListHead);
                slot->NextFree = (uint)head;

                long next = ((head >> 32) + 1) << 32 | index;
                if (Interlocked.CompareExchange(ref s_freeListHead, next, head) == head)
                {
                    return;
                }
            }
        }

        private static OBJECTHANDLE CreateHandle(byte* obj, HandleType type, void* extraInfo)
        {
            HandleSlot* slot = AllocateSlot();
            if (slot == null)
            {
                return default;
            }

            slot->Type = type;
            slot->ExtraInfo = (nint)extraInfo;

            // Published last so that the handle is never visible holding a stale object.
            Volatile.Write(ref slot->Object, (nint)obj);
            return new OBJECTHANDLE(slot);
        }

        // ------------------------------------------------------------------------------------
        // IGCHandleManager
        // ------------------------------------------------------------------------------------

        private static byte Initialize(void* thisPtr)
        {
            nuint size = (nuint)(sizeof(HandleSlot) * MaxHandles);
            byte* table = GCToOSInterface.VirtualReserve(size, 0);
            if (table == null || !GCToOSInterface.VirtualCommit(table, size))
            {
                return 0;
            }

            s_slots = (HandleSlot*)table;
            return 1;
        }

        private static void Shutdown(void* thisPtr)
        {
        }

        private static void* GetGlobalHandleStore(void* thisPtr) => Unsafe.AsPointer(ref s_storeVtablePtr);

        /// <summary>
        /// There is only one handle store, because there is only one heap and nothing scans
        /// handles per-store. The EE never destroys the global store, so handing it back here is
        /// safe as long as <see cref="DestroyHandleStore"/> stays a no-op.
        /// </summary>
        private static void* CreateHandleStore(void* thisPtr) => Unsafe.AsPointer(ref s_storeVtablePtr);

        private static void DestroyHandleStore(void* thisPtr, void* store)
        {
        }

        private static OBJECTHANDLE CreateGlobalHandleOfType(void* thisPtr, byte* obj, HandleType type) =>
            CreateHandle(obj, type, null);

        private static OBJECTHANDLE CreateDuplicateHandle(void* thisPtr, OBJECTHANDLE handle)
        {
            HandleSlot* slot = (HandleSlot*)handle.Value;
            return CreateHandle((byte*)Volatile.Read(ref slot->Object), slot->Type, (void*)slot->ExtraInfo);
        }

        private static void DestroyHandleOfType(void* thisPtr, OBJECTHANDLE handle, HandleType type) =>
            FreeSlot((HandleSlot*)handle.Value);

        private static void DestroyHandleOfUnknownType(void* thisPtr, OBJECTHANDLE handle) =>
            FreeSlot((HandleSlot*)handle.Value);

        private static void SetExtraInfoForHandle(void* thisPtr, OBJECTHANDLE handle, HandleType type, void* pExtraInfo) =>
            ((HandleSlot*)handle.Value)->ExtraInfo = (nint)pExtraInfo;

        private static void* GetExtraInfoFromHandle(void* thisPtr, OBJECTHANDLE handle) =>
            (void*)((HandleSlot*)handle.Value)->ExtraInfo;

        private static void StoreObjectInHandle(void* thisPtr, OBJECTHANDLE handle, byte* obj) =>
            Volatile.Write(ref ((HandleSlot*)handle.Value)->Object, (nint)obj);

        private static byte StoreObjectInHandleIfNull(void* thisPtr, OBJECTHANDLE handle, byte* obj) =>
            Interlocked.CompareExchange(ref *(nint*)handle.Value, (nint)obj, 0) == 0 ? (byte)1 : (byte)0;

        private static void SetDependentHandleSecondary(void* thisPtr, OBJECTHANDLE handle, byte* obj) =>
            ((HandleSlot*)handle.Value)->ExtraInfo = (nint)obj;

        private static byte* GetDependentHandleSecondary(void* thisPtr, OBJECTHANDLE handle) =>
            (byte*)((HandleSlot*)handle.Value)->ExtraInfo;

        private static byte* InterlockedCompareExchangeObjectInHandle(void* thisPtr, OBJECTHANDLE handle, byte* obj, byte* comparandObject) =>
            (byte*)Interlocked.CompareExchange(ref *(nint*)handle.Value, (nint)obj, (nint)comparandObject);

        private static HandleType HandleFetchType(void* thisPtr, OBJECTHANDLE handle) =>
            ((HandleSlot*)handle.Value)->Type;

        private static void TraceRefCountedHandles(void* thisPtr, delegate* unmanaged<byte**, nuint*, nuint, nuint, void> callback, nuint lp1, nuint lp2)
        {
        }

        // ------------------------------------------------------------------------------------
        // IGCHandleStore
        // ------------------------------------------------------------------------------------

        private static void Uproot(void* thisPtr)
        {
        }

        private static byte ContainsHandle(void* thisPtr, OBJECTHANDLE handle) =>
            handle.Value >= s_slots && handle.Value < s_slots + MaxHandles ? (byte)1 : (byte)0;

        private static OBJECTHANDLE CreateHandleOfType(void* thisPtr, byte* obj, HandleType type) =>
            CreateHandle(obj, type, null);

        private static OBJECTHANDLE CreateHandleOfType_2(void* thisPtr, byte* obj, HandleType type, int heapToAffinitizeTo) =>
            CreateHandle(obj, type, null);

        private static OBJECTHANDLE CreateHandleWithExtraInfo(void* thisPtr, byte* obj, HandleType type, void* pExtraInfo) =>
            CreateHandle(obj, type, pExtraInfo);

        private static OBJECTHANDLE CreateDependentHandle(void* thisPtr, byte* primary, byte* secondary) =>
            CreateHandle(primary, HandleType.HNDTYPE_DEPENDENT, secondary);
    }
}
