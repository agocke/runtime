// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/gcbridge.cpp.

#if FEATURE_JAVAMARSHAL

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe class GCBridge
{
    private const nuint BridgeMarkedBit = 2;
    private const int Initial = 0;
    private const int Scanned = 1;
    private const int FinishedOnStack = 2;
    private const int FinishedOffStack = 3;
    private const int HeavyRefsMin = 2;
    private const int HeavyCombinedRefsMin = 60;
    private const nuint BucketSize = 8184;
    private const int ElementsPerCacheBucket = 8;
    private const int ColorCacheSize = 128;

    private struct DynPtrArray
    {
        public int size;
        public int capacity;
        public void** data;
    }

    private struct ColorData
    {
        public DynPtrArray otherColors;
        public DynPtrArray bridges;
        public int apiIndex;
        public uint incomingColors;
        public byte visited;
        public byte visibleToClient;
    }

    private struct ScanData
    {
        public byte* obj;
        public nuint headerWord;
        public nuint context;
        public ColorData* color;
        public DynPtrArray xrefs;
        public int index;
        public int lowIndex;
        public byte state;
        public byte isBridge;
    }

    private struct DataBucket
    {
        public DataBucket* next;
        public nuint count;
    }

    private struct HashEntry
    {
        public ColorData* color;
        public uint hash;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MergeCache
    {
        public nuint alignment;
        public fixed byte data[
            ColorCacheSize * ElementsPerCacheBucket * 16];
    }

    private static DynPtrArray s_scanStack;
    private static DynPtrArray s_loopStack;
    private static DynPtrArray s_registeredBridges;
    private static DynPtrArray s_registeredBridgeContexts;
    private static DynPtrArray s_colorMergeArray;
    private static DynPtrArray s_scanData;
    private static DynPtrArray s_colorData;
    private static nuint s_rootScanBucket;
    private static nuint s_currentScanBucket;
    private static nuint s_rootColorBucket;
    private static nuint s_currentColorBucket;
    private static int s_objectIndex;
    private static int s_numSccs;
    private static int s_xrefCount;
    private static uint s_colorMergeArrayHash;
    private static uint s_hashPerturb;
    private static MergeCache s_mergeCache;

    private static DynPtrArray* ArrayPointer(ref DynPtrArray array) =>
        (DynPtrArray*)Unsafe.AsPointer(ref array);

    private static DataBucket** BucketPointer(ref nuint bucket) =>
        (DataBucket**)Unsafe.AsPointer(ref bucket);

    private static HashEntry* MergeCacheEntries =>
        (HashEntry*)((byte*)Unsafe.AsPointer(ref s_mergeCache) +
            sizeof(nuint));

    private static void DynPtrArrayEmpty(DynPtrArray* array)
    {
        array->size = 0;
    }

    private static bool DynPtrArrayEnsureCapacity(
        DynPtrArray* array,
        int capacity)
    {
        if (capacity <= array->capacity)
        {
            return true;
        }

        int newCapacity = array->capacity <= 0 ? 2 : array->capacity;
        while (newCapacity < capacity)
        {
            if (newCapacity > int.MaxValue / 2)
            {
                return false;
            }

            newCapacity *= 2;
        }

        nuint byteCount = (nuint)newCapacity * (nuint)sizeof(void*);
        void** data = (void**)SyncImports.ManagedGC_AllocZeroed(byteCount);
        if (data is null)
        {
            return false;
        }

        if (array->size != 0)
        {
            Buffer.MemoryCopy(
                array->data,
                data,
                byteCount,
                (nuint)array->size * (nuint)sizeof(void*));
        }

        if (array->data is not null)
        {
            SyncImports.ManagedGC_Free(array->data);
        }

        array->data = data;
        array->capacity = newCapacity;
        return true;
    }

    private static bool DynPtrArrayAdd(DynPtrArray* array, void* value)
    {
        if (!DynPtrArrayEnsureCapacity(array, array->size + 1))
        {
            return false;
        }

        array->data[array->size++] = value;
        return true;
    }

    private static void* DynPtrArrayGet(DynPtrArray* array, int index)
    {
        Debug.Assert((uint)index < (uint)array->size);
        return array->data[index];
    }

    private static void* DynPtrArrayPop(DynPtrArray* array)
    {
        Debug.Assert(array->size > 0);
        return array->data[--array->size];
    }

    private static bool DynPtrArrayContains(
        DynPtrArray* array,
        void* value)
    {
        for (int i = 0; i < array->size; i++)
        {
            if (array->data[i] == value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DynPtrArraySetAll(
        DynPtrArray* destination,
        DynPtrArray* source)
    {
        if (source->size != 0 &&
            !DynPtrArrayEnsureCapacity(destination, source->size))
        {
            return false;
        }

        if (source->size != 0)
        {
            Buffer.MemoryCopy(
                source->data,
                destination->data,
                (nuint)destination->capacity * (nuint)sizeof(void*),
                (nuint)source->size * (nuint)sizeof(void*));
        }

        destination->size = source->size;
        return true;
    }

    private static void DynPtrArrayUninit(DynPtrArray* array)
    {
        if (array->data is not null)
        {
            SyncImports.ManagedGC_Free(array->data);
        }

        *array = default;
    }

    private static ScanData* AllocateScanData()
    {
        void* allocation = AllocateFromBucket(
            BucketPointer(ref s_rootScanBucket),
            BucketPointer(ref s_currentScanBucket),
            (nuint)sizeof(ScanData));
        if (allocation is null)
        {
            return null;
        }

        ScanData* data = (ScanData*)allocation;
        *data = default;
        if (!DynPtrArrayAdd(ArrayPointer(ref s_scanData), data))
        {
            return null;
        }

        data->index = -1;
        data->lowIndex = -1;
        return data;
    }

    private static ColorData* AllocateColorData()
    {
        void* allocation = AllocateFromBucket(
            BucketPointer(ref s_rootColorBucket),
            BucketPointer(ref s_currentColorBucket),
            (nuint)sizeof(ColorData));
        if (allocation is null)
        {
            return null;
        }

        ColorData* data = (ColorData*)allocation;
        *data = default;
        if (!DynPtrArrayAdd(ArrayPointer(ref s_colorData), data))
        {
            return null;
        }

        data->apiIndex = -1;
        return data;
    }

    private static void* AllocateFromBucket(
        DataBucket** root,
        DataBucket** current,
        nuint elementSize)
    {
        nuint capacity =
            (BucketSize - (nuint)sizeof(DataBucket)) / elementSize;
        if (*current is null)
        {
            *root = *current =
                (DataBucket*)SyncImports.ManagedGC_AllocZeroed(BucketSize);
            if (*current is null)
            {
                return null;
            }
        }

        if ((*current)->count == capacity)
        {
            if ((*current)->next is null)
            {
                (*current)->next =
                    (DataBucket*)SyncImports.ManagedGC_AllocZeroed(BucketSize);
                if ((*current)->next is null)
                {
                    return null;
                }
            }

            *current = (*current)->next;
        }

        byte* data = (byte*)(*current + 1);
        void* result = data + ((*current)->count * elementSize);
        (*current)->count++;
        return result;
    }

    private static void ResetBuckets(
        DataBucket* root,
        DataBucket** current)
    {
        for (DataBucket* bucket = root;
             bucket is not null;
             bucket = bucket->next)
        {
            bucket->count = 0;
        }

        *current = root;
    }

    private static ScanData* CreateData(byte* obj)
    {
        ScanData* data = AllocateScanData();
        if (data is null)
        {
            return null;
        }

        nuint* words = (nuint*)obj;
        data->obj = obj;
        data->headerWord = words[-1];
        words[0] |= BridgeMarkedBit;
        words[-1] = (nuint)data;
        return data;
    }

    private static ScanData* FindData(byte* obj)
    {
        nuint* words = (nuint*)obj;
        return (words[0] & BridgeMarkedBit) != 0
            ? (ScanData*)words[-1]
            : null;
    }

    private static void ResetObjectHeaders()
    {
        for (int i = 0; i < s_scanData.size; i++)
        {
            ScanData* data = (ScanData*)s_scanData.data[i];
            nuint* words = (nuint*)data->obj;
            words[0] &= ~BridgeMarkedBit;
            words[-1] = data->headerWord;
        }
    }

    private static void ReleaseScanData()
    {
        for (int i = 0; i < s_scanData.size; i++)
        {
            ScanData* data = (ScanData*)s_scanData.data[i];
            DynPtrArrayUninit(ArrayPointer(ref data->xrefs));
        }

        DynPtrArrayEmpty(ArrayPointer(ref s_scanData));
        ResetBuckets(
            (DataBucket*)s_rootScanBucket,
            BucketPointer(ref s_currentScanBucket));
    }

    private static void ReleaseColorData()
    {
        for (int i = 0; i < s_colorData.size; i++)
        {
            ColorData* data = (ColorData*)s_colorData.data[i];
            DynPtrArrayUninit(ArrayPointer(ref data->otherColors));
            DynPtrArrayUninit(ArrayPointer(ref data->bridges));
        }

        DynPtrArrayEmpty(ArrayPointer(ref s_colorData));
        ResetBuckets(
            (DataBucket*)s_rootColorBucket,
            BucketPointer(ref s_currentColorBucket));
    }

    public static void BridgeResetData()
    {
        DynPtrArrayEmpty(ArrayPointer(ref s_registeredBridges));
        DynPtrArrayEmpty(ArrayPointer(ref s_registeredBridgeContexts));
        DynPtrArrayEmpty(ArrayPointer(ref s_scanStack));
        DynPtrArrayEmpty(ArrayPointer(ref s_loopStack));
        DynPtrArrayEmpty(ArrayPointer(ref s_colorMergeArray));
        ReleaseScanData();
        ReleaseColorData();
        s_objectIndex = 0;
        s_numSccs = 0;
        s_xrefCount = 0;
        s_colorMergeArrayHash = 0;
        HashEntry* cache = MergeCacheEntries;
        for (int i = 0;
             i < ColorCacheSize * ElementsPerCacheBucket;
             i++)
        {
            cache[i] = default;
        }

        s_hashPerturb++;
    }

    public static void RegisterBridgeObject(byte* obj, nuint context)
    {
        DynPtrArray* bridges =
            ArrayPointer(ref s_registeredBridges);
        DynPtrArray* contexts =
            ArrayPointer(ref s_registeredBridgeContexts);
        int count = bridges->size + 1;
        if (!DynPtrArrayEnsureCapacity(bridges, count) ||
            !DynPtrArrayEnsureCapacity(contexts, count))
        {
            return;
        }

        bridges->data[bridges->size++] = obj;
        contexts->data[contexts->size++] = (void*)context;
    }

    public static byte** GetRegisteredBridges(nuint* count)
    {
        *count = (nuint)s_registeredBridges.size;
        return (byte**)s_registeredBridges.data;
    }

    private static byte PushObject(byte* obj, void* context)
    {
        _ = context;
        ScanData* data = FindData(obj);
        if (data is not null && data->state != Initial)
        {
            return 1;
        }

        if (data is null && ManagedGCHeap.IsPromotedForBridge(obj))
        {
            return 1;
        }

        if (data is null)
        {
            data = CreateData(obj);
            if (data is null)
            {
                return 0;
            }
        }

        return DynPtrArrayAdd(
            ArrayPointer(ref s_scanStack),
            data) ? (byte)1 : (byte)0;
    }

    private static void PushAll(ScanData* data)
    {
        ManagedGCHeap.DiagWalkObjectForBridge(
            data->obj,
            &PushObject,
            null);
    }

    private static byte ComputeLowIndex(byte* obj, void* context)
    {
        ScanData* data = (ScanData*)context;
        ScanData* other = FindData(obj);
        if (other is null)
        {
            return 1;
        }

        if ((other->state == Scanned ||
             other->state == FinishedOnStack) &&
            data->lowIndex > other->lowIndex)
        {
            data->lowIndex = other->lowIndex;
        }

        ColorData* color = other->color;
        if (color is not null && color->visited == 0)
        {
            _ = DynPtrArrayAdd(
                ArrayPointer(ref s_colorMergeArray),
                color);
            s_colorMergeArrayHash += MixHash((nuint)color);
            color->visited = 1;
        }

        return 1;
    }

    private static uint MixHash(nuint source)
    {
        uint hash = (uint)source ^ s_hashPerturb;
        hash = ((hash * 215497) >> 16) ^
            ((hash * 1823231) + hash);
        if (sizeof(nuint) > sizeof(uint))
        {
            hash ^= (uint)((source >> 31) >> 1);
        }

        return hash;
    }

    private static bool MatchColors(
        DynPtrArray* first,
        DynPtrArray* second)
    {
        if (first->size != second->size)
        {
            return false;
        }

        for (int i = 0; i < first->size; i++)
        {
            if (!DynPtrArrayContains(second, first->data[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static ColorData* FindInCache(out int insertIndex)
    {
        uint hash = s_colorMergeArrayHash;
        if (hash == 0)
        {
            hash = 1;
        }

        int cacheBucket = (int)(hash & (ColorCacheSize - 1));
        HashEntry* entries =
            MergeCacheEntries +
            (cacheBucket * ElementsPerCacheBucket);
        bool estimateOnly = s_colorMergeArray.size > 3;
        for (int i = 0; i < ElementsPerCacheBucket; i++)
        {
            if (entries[i].hash != hash)
            {
                continue;
            }

            if (estimateOnly
                    ? entries[i].color->otherColors.size ==
                        s_colorMergeArray.size
                    : MatchColors(
                        ArrayPointer(ref entries[i].color->otherColors),
                        ArrayPointer(ref s_colorMergeArray)))
            {
                insertIndex = -1;
                return entries[i].color;
            }
        }

        for (int i = ElementsPerCacheBucket - 1; i > 0; i--)
        {
            entries[i] = entries[i - 1];
        }

        entries[0].hash = hash;
        insertIndex = cacheBucket;
        return null;
    }

    private static ColorData* NewColor(bool hasBridges)
    {
        int cacheBucket = -1;
        if (!hasBridges)
        {
            ColorData* cached = FindInCache(out cacheBucket);
            if (cached is not null)
            {
                return cached;
            }
        }

        ColorData* color = AllocateColorData();
        if (color is null)
        {
            return null;
        }

        for (int i = 0; i < s_colorMergeArray.size; i++)
        {
            ColorData* target =
                (ColorData*)s_colorMergeArray.data[i];
            _ = DynPtrArrayAdd(
                ArrayPointer(ref color->otherColors),
                target);
            target->incomingColors++;
        }

        if (cacheBucket >= 0)
        {
            MergeCacheEntries[
                cacheBucket * ElementsPerCacheBucket].color = color;
        }

        return color;
    }

    private static ColorData* ReduceColor()
    {
        return s_colorMergeArray.size switch
        {
            0 => null,
            1 => (ColorData*)s_colorMergeArray.data[0],
            _ => NewColor(hasBridges: false),
        };
    }

    private static bool CreateScc(ScanData* root)
    {
        bool foundBridge = false;
        bool hasXrefs = false;
        for (int i = s_loopStack.size - 1; i >= 0; i--)
        {
            ScanData* data = (ScanData*)s_loopStack.data[i];
            foundBridge |= data->isBridge != 0;
            hasXrefs |= data->xrefs.size != 0;
            if (data == root)
            {
                break;
            }
        }

        ColorData* color = foundBridge
            ? NewColor(hasBridges: true)
            : hasXrefs
                ? NewColor(hasBridges: false)
                : ReduceColor();
        if ((foundBridge || hasXrefs) && color is null)
        {
            return false;
        }

        while (s_loopStack.size != 0)
        {
            ScanData* data = (ScanData*)DynPtrArrayPop(
                ArrayPointer(ref s_loopStack));
            data->color = color;
            data->state = FinishedOffStack;

            if (data->isBridge != 0)
            {
                Debug.Assert(color is not null);
                if (!DynPtrArrayAdd(
                    ArrayPointer(ref color->bridges),
                    data->obj))
                {
                    return false;
                }
            }

            if (data->xrefs.size != 0)
            {
                Debug.Assert(color is not null);
                for (int i = 0; i < data->xrefs.size; i++)
                {
                    ColorData* target =
                        (ColorData*)data->xrefs.data[i];
                    if (!DynPtrArrayContains(
                            ArrayPointer(ref color->otherColors),
                            target))
                    {
                        if (!DynPtrArrayAdd(
                                ArrayPointer(ref color->otherColors),
                                target))
                        {
                            return false;
                        }

                        target->incomingColors++;
                    }
                }
            }

            DynPtrArrayUninit(ArrayPointer(ref data->xrefs));
            if (data == root)
            {
                return true;
            }
        }

        Debug.Assert(false);
        return false;
    }

    private static bool DepthFirstSearch()
    {
        DynPtrArrayEmpty(ArrayPointer(ref s_colorMergeArray));
        s_colorMergeArrayHash = 0;
        while (s_scanStack.size != 0)
        {
            ScanData* data = (ScanData*)DynPtrArrayPop(
                ArrayPointer(ref s_scanStack));
            if (data->state is FinishedOnStack or FinishedOffStack)
            {
                continue;
            }

            if (data->state == Initial)
            {
                data->state = Scanned;
                data->lowIndex = data->index = s_objectIndex++;
                if (!DynPtrArrayAdd(
                        ArrayPointer(ref s_scanStack),
                        data) ||
                    !DynPtrArrayAdd(
                        ArrayPointer(ref s_loopStack),
                        data))
                {
                    return false;
                }

                PushAll(data);
                continue;
            }

            data->state = FinishedOnStack;
            ManagedGCHeap.DiagWalkObjectForBridge(
                data->obj,
                &ComputeLowIndex,
                data);

            if (data->index == data->lowIndex)
            {
                if (!CreateScc(data))
                {
                    return false;
                }
            }
            else if (!DynPtrArraySetAll(
                ArrayPointer(ref data->xrefs),
                ArrayPointer(ref s_colorMergeArray)))
            {
                return false;
            }

            for (int i = 0; i < s_colorMergeArray.size; i++)
            {
                ((ColorData*)s_colorMergeArray.data[i])->visited = 0;
            }

            DynPtrArrayEmpty(ArrayPointer(ref s_colorMergeArray));
            s_colorMergeArrayHash = 0;
        }

        return true;
    }

    private static bool TarjanSccAlgorithm()
    {
        int bridgeCount = s_registeredBridges.size;
        if (bridgeCount == 0)
        {
            return false;
        }

        for (int i = 0; i < bridgeCount; i++)
        {
            byte* obj = (byte*)s_registeredBridges.data[i];
            ScanData* data = CreateData(obj);
            if (data is null)
            {
                return false;
            }

            data->isBridge = 1;
            data->context =
                (nuint)s_registeredBridgeContexts.data[i];
        }

        for (int i = 0; i < bridgeCount; i++)
        {
            ScanData* data = FindData(
                (byte*)s_registeredBridges.data[i]);
            if (data->state == Initial)
            {
                if (!DynPtrArrayAdd(
                        ArrayPointer(ref s_scanStack),
                        data) ||
                    !DepthFirstSearch())
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ColorVisibleToClient(ColorData* color)
    {
        if (color->visibleToClient != 0)
        {
            return true;
        }

        int fanin = (int)color->incomingColors;
        int fanout = color->otherColors.size;
        if (color->bridges.size != 0 ||
            (fanin > HeavyRefsMin &&
             fanout > HeavyRefsMin &&
             fanin * fanout >= HeavyCombinedRefsMin))
        {
            color->visibleToClient = 1;
            return true;
        }

        return false;
    }

    private static bool GatherXRefs(ColorData* color)
    {
        for (int i = 0; i < color->otherColors.size; i++)
        {
            ColorData* target =
                (ColorData*)color->otherColors.data[i];
            if (target->visited != 0)
            {
                continue;
            }

            target->visited = 1;
            if (ColorVisibleToClient(target))
            {
                if (!DynPtrArrayAdd(
                    ArrayPointer(ref s_colorMergeArray),
                    target))
                {
                    return false;
                }
            }
            else if (!GatherXRefs(target))
            {
                return false;
            }
        }

        return true;
    }

    private static void ResetXRefs(ColorData* color)
    {
        for (int i = 0; i < color->otherColors.size; i++)
        {
            ColorData* target =
                (ColorData*)color->otherColors.data[i];
            if (target->visited == 0)
            {
                continue;
            }

            target->visited = 0;
            if (!ColorVisibleToClient(target))
            {
                ResetXRefs(target);
            }
        }
    }

    private static MarkCrossReferencesArgs* BuildSccCallbackData()
    {
        s_numSccs = 0;
        for (int i = 0; i < s_colorData.size; i++)
        {
            if (ColorVisibleToClient((ColorData*)s_colorData.data[i]))
            {
                s_numSccs++;
            }
        }

        StronglyConnectedComponent* components =
            (StronglyConnectedComponent*)SyncImports.ManagedGC_AllocZeroed(
                (nuint)s_numSccs *
                (nuint)sizeof(StronglyConnectedComponent));
        if (components is null)
        {
            return null;
        }

        int apiIndex = 0;
        for (int i = 0; i < s_colorData.size; i++)
        {
            ColorData* color = (ColorData*)s_colorData.data[i];
            if (!ColorVisibleToClient(color))
            {
                continue;
            }

            components[apiIndex].Count = (nuint)color->bridges.size;
            if (color->bridges.size != 0)
            {
                components[apiIndex].Contexts =
                    (nuint*)SyncImports.ManagedGC_AllocZeroed(
                        (nuint)color->bridges.size *
                        (nuint)sizeof(nuint));
                if (components[apiIndex].Contexts is null)
                {
                    return null;
                }

                for (int bridge = 0;
                     bridge < color->bridges.size;
                     bridge++)
                {
                    ScanData* data = FindData(
                        (byte*)color->bridges.data[bridge]);
                    components[apiIndex].Contexts[bridge] =
                        data->context;
                }
            }

            color->apiIndex = apiIndex++;
        }

        s_xrefCount = 0;
        for (int i = 0; i < s_colorData.size; i++)
        {
            ColorData* color = (ColorData*)s_colorData.data[i];
            if (!ColorVisibleToClient(color))
            {
                continue;
            }

            DynPtrArrayEmpty(ArrayPointer(ref s_colorMergeArray));
            s_colorMergeArrayHash = 0;
            if (!GatherXRefs(color))
            {
                return null;
            }

            ResetXRefs(color);
            if (!DynPtrArraySetAll(
                ArrayPointer(ref color->otherColors),
                ArrayPointer(ref s_colorMergeArray)))
            {
                return null;
            }

            s_xrefCount += color->otherColors.size;
        }

        ComponentCrossReference* crossReferences =
            (ComponentCrossReference*)SyncImports.ManagedGC_AllocZeroed(
                (nuint)s_xrefCount *
                (nuint)sizeof(ComponentCrossReference));
        if (s_xrefCount != 0 && crossReferences is null)
        {
            return null;
        }

        int xrefIndex = 0;
        for (int i = 0; i < s_colorData.size; i++)
        {
            ColorData* source = (ColorData*)s_colorData.data[i];
            if (!ColorVisibleToClient(source))
            {
                continue;
            }

            for (int targetIndex = 0;
                 targetIndex < source->otherColors.size;
                 targetIndex++)
            {
                ColorData* target =
                    (ColorData*)source->otherColors.data[targetIndex];
                crossReferences[xrefIndex].SourceGroupIndex =
                    (nuint)source->apiIndex;
                crossReferences[xrefIndex].DestinationGroupIndex =
                    (nuint)target->apiIndex;
                xrefIndex++;
            }
        }

        MarkCrossReferencesArgs* args =
            (MarkCrossReferencesArgs*)SyncImports.ManagedGC_AllocZeroed(
                (nuint)sizeof(MarkCrossReferencesArgs));
        if (args is null)
        {
            return null;
        }

        args->ComponentCount = (nuint)s_numSccs;
        args->Components = components;
        args->CrossReferenceCount = (nuint)s_xrefCount;
        args->CrossReferences = crossReferences;
        return args;
    }

    public static MarkCrossReferencesArgs* ProcessBridgeObjects()
    {
        if (!TarjanSccAlgorithm())
        {
            if (s_scanData.size != 0)
            {
                ResetObjectHeaders();
            }

            return null;
        }

        MarkCrossReferencesArgs* args = BuildSccCallbackData();
        ResetObjectHeaders();
        return args;
    }
}

#endif
