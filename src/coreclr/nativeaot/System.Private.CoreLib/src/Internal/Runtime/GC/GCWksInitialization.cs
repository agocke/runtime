// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GC
{
    internal static unsafe partial class GCWksInitialization
    {
        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const nuint LargeObjectSize = 85000;
        private const nuint InitialSegmentSize = 256 * 1024 * 1024;
        private const nuint LargeObjectSegmentSize = 128 * 1024 * 1024;
        private const nuint GCPageSize = 0x1000;
        private const nuint CardWordWidth = 32;
        private static readonly nuint CardSize = sizeof(void*) == 8 ? 2 * GCPageSize / CardWordWidth : GCPageSize / CardWordWidth;
        private const nuint CardBundleSize = GCPageSize / (sizeof(uint) * CardWordWidth);
        private const nuint CardBundleWordWidth = 32;
        private static readonly nuint CardBundleWordCoverage = CardSize * CardWordWidth * CardBundleSize * CardBundleWordWidth;
        private const nuint BrickSize = 4096;
        private static readonly nuint MinObjectSize = (nuint)(2 * sizeof(void*) + sizeof(ObjHeader));
        private const nuint AllocationQuantum = 8 * 1024 + 32;
        private const nuint SegmentAlignment = 4 * 1024 * 1024;
        private const nuint SegmentInitialCommit = 4096;
        private const int InitialFinalizerArraySize = 100;
        private const int FinalizationExtraSegmentCount = 2;
        private const int FinalizerCriticalListSegment = (int)gc_generation_num.total_generation_count;
        private const int FinalizerListSegment = FinalizerCriticalListSegment + 1;
        private const int FinalizerFreeListSegment = FinalizerListSegment + (FinalizationExtraSegmentCount - 1);
        private static readonly nuint LohPaddingSize = 4 * (nuint)sizeof(void*);
        private const uint UnsupportedAllocationFlags = (uint)GC_ALLOC_FLAGS.GC_ALLOC_ALIGN8_BIAS;
        private const uint SupportedAllocationFlags =
            (uint)(GC_ALLOC_FLAGS.GC_ALLOC_FINALIZE |
                   GC_ALLOC_FLAGS.GC_ALLOC_CONTAINS_REF |
                   GC_ALLOC_FLAGS.GC_ALLOC_ALIGN8 |
                   GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL |
                   GC_ALLOC_FLAGS.GC_ALLOC_LARGE_OBJECT_HEAP |
                   GC_ALLOC_FLAGS.GC_ALLOC_PINNED_OBJECT_HEAP);

        private static nuint s_sohSegmentSize;
        private static nuint s_minUohSegmentSize;
        private static nuint s_lohThreshold;
        private static byte* s_heapBase;
        private static byte* s_heapEnd;
        private static uint* s_cardTable;
        private static uint* s_cardBundleTable;
        private static CLRCriticalSection s_sohAllocationLock;
        private static bool s_sohAllocationLockInitialized;
        private static CLRCriticalSection s_uohAllocationLock;
        private static bool s_uohAllocationLockInitialized;
        private static CLRCriticalSection s_frozenSegmentLock;
        private static bool s_frozenSegmentLockInitialized;
        private static heap_segment s_sohSegment;
        private static heap_segment s_lohSegment;
        private static heap_segment s_pohSegment;
        private static generation s_generation0;
        private static generation s_generation1;
        private static generation s_generation2;
        private static generation s_lohGeneration;
        private static generation s_pohGeneration;
        private static DynamicDataArray5 s_dynamicDataTable;
        private static byte* s_demotionLow;
        private static nuint s_maxgenPinnedCompactBeforeAdvance;
        private static bool s_decidePromoteGen1Pins;
        private static frozen_segment_entry* s_frozenSegmentLookup;
        private static CFinalize* s_finalizeQueue;
        private static ulong s_totalAllocatedBytesSoh;
        private static ulong s_totalAllocatedBytesUoh;

        public static int Initialize()
        {
            GCConfig.Initialize();
            if (GCConfig.HasUnsupportedHardLimitConfiguration())
            {
                return E_NOTIMPL;
            }

            if (!GCToOSInterface.Initialize())
            {
                return E_FAIL;
            }

            GCCommon.Initialize();
            GCCommon.g_num_processors = GCToOSInterface.GetTotalProcessorCount();
            if (GCCommon.g_num_processors == 0)
            {
                return E_FAIL;
            }

            // BACKGROUND_GC is not part of the supported WKS configuration.
            GCConfig.SetConcurrentGC(false);

            IGCToCLR* callback = GCCommon.g_theGCToCLR;
            if (callback is not null && callback->Vtable is not null && callback->Vtable->GetFreeObjectMethodTable is not null)
            {
                GCCommon.g_gc_pFreeObjectMethodTable = callback->Vtable->GetFreeObjectMethodTable(callback);
            }

            return S_OK;
        }

        public static int InitializeHeap(IGCHeap* heap)
        {
            if (GCConfig.HasUnsupportedHardLimitConfiguration())
            {
                return E_NOTIMPL;
            }

            s_sohSegmentSize = ComputeValidSegmentSize(false);
            s_minUohSegmentSize = ComputeValidSegmentSize(true);

            long lohThreshold = GCConfig.GetLOHThreshold();
            s_lohThreshold = lohThreshold < (long)LargeObjectSize ? LargeObjectSize : (nuint)lohThreshold;

            int result = InitializeHeapState();
            if (result == S_OK)
            {
                GCCommon.g_theGCHeap = heap;
            }

            return result;
        }

        public static bool IsValidSegmentSize(IGCHeap* heap, nuint size)
        {
            _ = heap;
            return IsValidSegmentSizeCore(size);
        }

        public static bool IsValidGen0MaxSize(IGCHeap* heap, nuint size)
        {
            _ = heap;
            return size >= 64 * 1024;
        }

        public static nuint GetValidSegmentSize(IGCHeap* heap, bool largeSegment)
        {
            _ = heap;
            return largeSegment ? s_minUohSegmentSize : s_sohSegmentSize;
        }

        public static bool IsConcurrentGCEnabled(IGCHeap* heap)
        {
            _ = heap;
            return false;
        }

        public static void Shutdown(IGCHeap* heap)
        {
            _ = heap;
            if (s_frozenSegmentLockInitialized)
            {
                s_frozenSegmentLock.Enter();
                ReleaseFrozenSegments();
                s_frozenSegmentLock.Leave();
                s_frozenSegmentLock.Destroy();
                s_frozenSegmentLockInitialized = false;
            }

            ReleaseFinalizationQueue();
        }

        public static void PublishObject(IGCHeap* heap, byte* obj)
        {
            _ = heap;
            _ = obj;
        }

        public static uint GetMaxGeneration(IGCHeap* heap)
        {
            _ = heap;
            return (uint)gc_generation_num.max_generation;
        }

        public static nuint GetLOHThreshold(IGCHeap* heap)
        {
            _ = heap;
            return s_lohThreshold;
        }

        public static uint WhichGeneration(IGCHeap* heap, Object* obj)
        {
            _ = heap;
            return GetGenerationWithRangeCore(obj, null, null, null);
        }

        public static nuint GetTotalBytesInUse(IGCHeap* heap)
        {
            _ = heap;
            if (s_sohAllocationLockInitialized)
            {
                s_sohAllocationLock.Enter();
            }

            if (s_uohAllocationLockInitialized)
            {
                s_uohAllocationLock.Enter();
            }

            fixed (heap_segment* sohSegment = &s_sohSegment)
            fixed (heap_segment* lohSegment = &s_lohSegment)
            fixed (heap_segment* pohSegment = &s_pohSegment)
            {
                nuint total = GetSegmentBytesInUse(
                    sohSegment,
                    s_generation0.free_obj_space + s_generation1.free_obj_space + s_generation2.free_obj_space,
                    s_generation0.free_list_space + s_generation1.free_list_space + s_generation2.free_list_space);
                total += GetSegmentBytesInUse(lohSegment, s_lohGeneration.free_obj_space, s_lohGeneration.free_list_space);
                total += GetSegmentBytesInUse(pohSegment, s_pohGeneration.free_obj_space, s_pohGeneration.free_list_space);

                if (s_uohAllocationLockInitialized)
                {
                    s_uohAllocationLock.Leave();
                }

                if (s_sohAllocationLockInitialized)
                {
                    s_sohAllocationLock.Leave();
                }

                return total;
            }
        }

        public static ulong GetTotalAllocatedBytes(IGCHeap* heap)
        {
            _ = heap;
            return System.Threading.Volatile.Read(ref s_totalAllocatedBytesSoh) +
                System.Threading.Volatile.Read(ref s_totalAllocatedBytesUoh);
        }

        public static uint GetGenerationWithRange(IGCHeap* heap, Object* obj, byte** start, byte** allocated, byte** reserved)
        {
            _ = heap;
            return GetGenerationWithRangeCore(obj, start, allocated, reserved);
        }

        public static void ControlEvents(IGCHeap* heap, GCEventKeyword keywords, GCEventLevel level)
        {
            _ = heap;
            GCCommon.SetPublicEventStatus(keywords, level);
        }

        public static void ControlPrivateEvents(IGCHeap* heap, GCEventKeyword keywords, GCEventLevel level)
        {
            _ = heap;
            GCCommon.SetPrivateEventStatus(keywords, level);
        }

        public static gc_heap_segment_stub* RegisterFrozenSegment(IGCHeap* heap, segment_info* segmentInfo)
        {
            _ = heap;
            if (!s_frozenSegmentLockInitialized ||
                segmentInfo is null ||
                segmentInfo->pvMem is null ||
                segmentInfo->ibFirstObject > segmentInfo->ibAllocated ||
                segmentInfo->ibAllocated > segmentInfo->ibCommit ||
                segmentInfo->ibCommit > segmentInfo->ibReserved ||
                !TryAddPointer((byte*)segmentInfo->pvMem, segmentInfo->ibFirstObject, out byte* memory) ||
                !TryAddPointer((byte*)segmentInfo->pvMem, segmentInfo->ibAllocated, out byte* allocated) ||
                !TryAddPointer((byte*)segmentInfo->pvMem, segmentInfo->ibCommit, out byte* committed) ||
                !TryAddPointer((byte*)segmentInfo->pvMem, segmentInfo->ibReserved, out byte* reserved))
            {
                return null;
            }

            heap_segment* segment = (heap_segment*)GCToOSInterface.AllocateUnmanaged((nuint)sizeof(heap_segment));
            if (segment is null)
            {
                return null;
            }

            frozen_segment_entry* entry = (frozen_segment_entry*)GCToOSInterface.AllocateUnmanaged((nuint)sizeof(frozen_segment_entry));
            if (entry is null)
            {
                GCToOSInterface.FreeUnmanaged(segment);
                return null;
            }

            *segment = default;
            segment->mem = memory;
            segment->allocated = allocated;
            segment->committed = committed;
            segment->reserved = reserved;
            segment->used = allocated;
            segment->flags = 1;

            s_frozenSegmentLock.Enter();
            if (FindFrozenSegmentByBase(memory) is not null)
            {
                s_frozenSegmentLock.Leave();
                GCToOSInterface.FreeUnmanaged(entry);
                GCToOSInterface.FreeUnmanaged(segment);
                return null;
            }

            segment->next = s_generation2.start_segment;
            s_generation2.start_segment = segment;
            entry->segment = segment;
            InsertFrozenSegmentLookup(entry);
            s_frozenSegmentLock.Leave();
            return (gc_heap_segment_stub*)segment;
        }

        public static void UnregisterFrozenSegment(IGCHeap* heap, gc_heap_segment_stub* handle)
        {
            _ = heap;
            if (!s_frozenSegmentLockInitialized)
            {
                return;
            }

            s_frozenSegmentLock.Enter();
            frozen_segment_entry* entry = FindFrozenSegmentByHandle((heap_segment*)handle);
            if (entry is null)
            {
                s_frozenSegmentLock.Leave();
                GCToOSInterface.DebugBreak();
                return;
            }

            RemoveFrozenSegmentFromGeneration(entry->segment);
            RemoveFrozenSegmentLookup(entry);
            GCToOSInterface.FreeUnmanaged(entry->segment);
            GCToOSInterface.FreeUnmanaged(entry);
            s_frozenSegmentLock.Leave();
        }

        public static bool IsInFrozenSegment(IGCHeap* heap, Object* obj)
        {
            _ = heap;
            if (obj is null)
            {
                return true;
            }

            nuint address = (nuint)obj;
            if (IsAddressInSegment(address, s_sohSegment) ||
                IsAddressInSegment(address, s_lohSegment) ||
                IsAddressInSegment(address, s_pohSegment))
            {
                return false;
            }

            if (!s_frozenSegmentLockInitialized)
            {
                return true;
            }

            s_frozenSegmentLock.Enter();
            bool result = FindFrozenSegmentContaining(address) is not null;
            s_frozenSegmentLock.Leave();
            return result;
        }

        public static void UpdateFrozenSegment(IGCHeap* heap, gc_heap_segment_stub* handle, byte* allocated, byte* committed)
        {
            _ = heap;
            if (!s_frozenSegmentLockInitialized)
            {
                return;
            }

            s_frozenSegmentLock.Enter();
            frozen_segment_entry* entry = FindFrozenSegmentByHandle((heap_segment*)handle);
            if (entry is null ||
                (nuint)allocated < (nuint)entry->segment->mem ||
                (nuint)allocated > (nuint)committed ||
                (nuint)committed > (nuint)entry->segment->reserved)
            {
                s_frozenSegmentLock.Leave();
                GCToOSInterface.DebugBreak();
                return;
            }

            entry->segment->allocated = allocated;
            entry->segment->committed = committed;
            s_frozenSegmentLock.Leave();
        }

        public static Object* Alloc(IGCHeap* heap, gc_alloc_context* context, nuint size, uint flags)
        {
            _ = heap;
            if (context is null)
            {
                return null;
            }

            if ((flags & UnsupportedAllocationFlags) != 0)
            {
                return null;
            }

            if ((flags & ~(SupportedAllocationFlags | UnsupportedAllocationFlags)) != 0)
            {
                return null;
            }

            if (size > nuint.MaxValue - ((nuint)sizeof(void*) - 1))
            {
                return null;
            }

            nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
            if (alignedSize < MinObjectSize)
            {
                alignedSize = MinObjectSize;
            }

            uint userOldHeapFlags = (uint)(GC_ALLOC_FLAGS.GC_ALLOC_LARGE_OBJECT_HEAP |
                                           GC_ALLOC_FLAGS.GC_ALLOC_PINNED_OBJECT_HEAP);
            if ((flags & userOldHeapFlags) != 0)
            {
                bool pinned = (flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_PINNED_OBJECT_HEAP) != 0;
                return RegisterAllocatedObject(AllocUohObject(context, alignedSize, flags, pinned), size, flags);
            }

            byte* allocation = context->alloc_ptr;
            byte* limit = context->alloc_limit;
            if (allocation is null || limit is null)
            {
                if (allocation is not null || limit is not null)
                {
                    return null;
                }
            }

            if (allocation is not null && allocation > limit)
            {
                return null;
            }

            if (allocation is not null &&
                IsAllocationAligned(allocation, flags) &&
                alignedSize <= (nuint)(limit - allocation))
            {
                context->alloc_ptr = allocation + alignedSize;
                context->alloc_count++;
                return RegisterAllocatedObject((Object*)allocation, size, flags);
            }

            s_sohAllocationLock.Enter();
            Object* result;
            if (allocation is not null && allocation > limit)
            {
                result = null;
            }
            else
            {
                result = GrantAllocationContext(context, alignedSize, flags);
            }

            s_sohAllocationLock.Leave();
            return RegisterAllocatedObject(result, size, flags);
        }

        private static Object* GrantAllocationContext(gc_alloc_context* context, nuint alignedSize, uint flags)
        {
            byte* previousAllocation = context->alloc_ptr;
            byte* previousLimit = context->alloc_limit;
            byte* allocation = s_sohSegment.allocated;

            if (previousAllocation is null != (previousLimit is null))
            {
                return null;
            }

            if (previousAllocation is not null && previousAllocation > previousLimit)
            {
                return null;
            }

            bool abandonedPreviousContext = false;
            bool contiguousAllocationContext = false;
            nuint unused = 0;
            nuint abandonedObjectSize = 0;
            byte* previousGrantEnd = null;
            if (previousAllocation is not null)
            {
                previousGrantEnd = previousLimit + MinObjectSize;
                unused = (nuint)(previousLimit - previousAllocation);
                abandonedPreviousContext = allocation != previousGrantEnd;
                contiguousAllocationContext = !abandonedPreviousContext;
                if (abandonedPreviousContext)
                {
                    if (GCCommon.g_gc_pFreeObjectMethodTable is null ||
                        !TryAdd(unused, MinObjectSize, out abandonedObjectSize) ||
                        abandonedObjectSize > uint.MaxValue + MinObjectSize ||
                        unused > (nuint)context->alloc_bytes)
                    {
                        return null;
                    }
                }
            }

            if (!IsAllocationAligned(allocation, flags))
            {
                return null;
            }

            nuint requestedWithTail;
            if (alignedSize > nuint.MaxValue - MinObjectSize)
            {
                return null;
            }

            requestedWithTail = alignedSize + MinObjectSize;
            nuint grantSize = (flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL) == 0
                ? (requestedWithTail < AllocationQuantum ? AllocationQuantum : requestedWithTail)
                : requestedWithTail;

            fixed (heap_segment* sohSegment = &s_sohSegment)
            {
                if (!CommitForAllocation(sohSegment, allocation, grantSize, s_generation0.allocation_start))
                {
                    return null;
                }
            }

            byte* grantEnd = allocation + grantSize;
            byte* limit = grantEnd - MinObjectSize;

            if ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL) == 0)
            {
                ClearMemory(allocation, grantSize);
            }

            if (abandonedPreviousContext)
            {
                FormatUnusedArray(previousAllocation, abandonedObjectSize);
                context->alloc_bytes -= (long)unused;
                s_totalAllocatedBytesSoh -= unused;
                s_generation0.free_obj_space += abandonedObjectSize;
            }
            else if (contiguousAllocationContext)
            {
                FormatUnusedArray(previousAllocation, MinObjectSize);
                previousAllocation += MinObjectSize;
            }

            context->alloc_ptr = contiguousAllocationContext ? previousAllocation : allocation;
            context->alloc_limit = limit;
            context->alloc_bytes += (long)(grantSize - MinObjectSize);
            s_totalAllocatedBytesSoh += grantSize - MinObjectSize;
            s_sohSegment.allocated = grantEnd;
            if (s_sohSegment.used < grantEnd)
            {
                s_sohSegment.used = grantEnd;
            }

            byte* result = context->alloc_ptr;
            ClearSyncBlock(result);
            context->alloc_ptr += alignedSize;
            context->alloc_count++;
            return (Object*)result;
        }

        private static void FormatUnusedArray(byte* address, nuint size)
        {
            ((Object*)address)->RawSetMethodTable(GCCommon.g_gc_pFreeObjectMethodTable);
            ((ArrayBase*)address)->m_dwLength = (uint)(size - MinObjectSize);
        }

        private static bool IsAllocationAligned(byte* allocation, uint flags)
        {
            if ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_ALIGN8) == 0)
            {
                return true;
            }

            return ((nuint)allocation & ((nuint)sizeof(void*) - 1)) == 0;
        }

        private static bool TryAdd(nuint left, nuint right, out nuint result)
        {
            if (left > nuint.MaxValue - right)
            {
                result = 0;
                return false;
            }

            result = left + right;
            return true;
        }

        private static bool TryMultiply(nuint left, nuint right, out nuint result)
        {
            if (left != 0 && right > nuint.MaxValue / left)
            {
                result = 0;
                return false;
            }

            result = left * right;
            return true;
        }

        private static bool TryAlignUp(nuint value, nuint alignment, out nuint result)
        {
            if (!TryAdd(value, alignment - 1, out nuint rounded))
            {
                result = 0;
                return false;
            }

            result = rounded & ~(alignment - 1);
            return result >= value;
        }

        private static bool TryAlignUp(byte* value, nuint alignment, out byte* result)
        {
            if (!TryAlignUp((nuint)value, alignment, out nuint rounded))
            {
                result = null;
                return false;
            }

            result = (byte*)rounded;
            return true;
        }

        private static bool TryGetCardBundleSize(byte* start, byte* end, out nuint size)
        {
            nuint first = GCEnvironment.AlignDown((nuint)start, CardBundleWordCoverage);
            if (!TryAlignUp((nuint)end, CardBundleWordCoverage, out nuint last) ||
                last < first ||
                !TryMultiply((last - first) / CardBundleWordCoverage, (nuint)sizeof(uint), out size))
            {
                size = 0;
                return false;
            }

            return true;
        }

        private static bool TryGetBookkeepingSize(
            byte* start,
            byte* end,
            out nuint cardBytes,
            out nuint brickBytes,
            out nuint cardBundleBytes,
            out nuint segmentMappingBytes,
            out nuint cardOffset,
            out nuint brickOffset,
            out nuint cardBundleOffset,
            out nuint segmentMappingOffset,
            out nuint allocationSize)
        {
            nuint firstCardWord = ((nuint)start / CardSize) / CardWordWidth;
            nuint lastCardWord = (((nuint)end - 1) / CardSize) / CardWordWidth;
            if (!TryMultiply(lastCardWord - firstCardWord + 1, (nuint)sizeof(uint), out cardBytes))
            {
                brickBytes = 0;
                cardBundleBytes = 0;
                segmentMappingBytes = 0;
                cardOffset = 0;
                brickOffset = 0;
                cardBundleOffset = 0;
                segmentMappingOffset = 0;
                allocationSize = 0;
                return false;
            }

            nuint firstBrick = GCEnvironment.AlignDown((nuint)start, BrickSize);
            if (!TryAlignUp((nuint)end, BrickSize, out nuint lastBrick) ||
                lastBrick < firstBrick ||
                !TryMultiply((lastBrick - firstBrick) / BrickSize, (nuint)sizeof(short), out brickBytes))
            {
                brickBytes = 0;
                cardBundleBytes = 0;
                segmentMappingBytes = 0;
                cardOffset = 0;
                brickOffset = 0;
                cardBundleOffset = 0;
                segmentMappingOffset = 0;
                allocationSize = 0;
                return false;
            }

            if (!TryGetCardBundleSize(start, end, out cardBundleBytes))
            {
                segmentMappingBytes = 0;
                cardOffset = 0;
                brickOffset = 0;
                cardBundleOffset = 0;
                segmentMappingOffset = 0;
                allocationSize = 0;
                return false;
            }

            nuint firstSegment = GCEnvironment.AlignDown((nuint)start, SegmentAlignment);
            if (!TryAlignUp((nuint)end, SegmentAlignment, out nuint lastSegment) ||
                lastSegment < firstSegment ||
                !TryMultiply((lastSegment - firstSegment) / SegmentAlignment, (nuint)sizeof(SegMapping), out segmentMappingBytes) ||
                !TryAlignUp((nuint)sizeof(CardTableInfo), (nuint)sizeof(uint), out cardOffset) ||
                !TryAdd(cardOffset, cardBytes, out nuint afterCardTable) ||
                !TryAlignUp(afterCardTable, (nuint)sizeof(short), out brickOffset) ||
                !TryAdd(brickOffset, brickBytes, out nuint afterBrickTable) ||
                !TryAlignUp(afterBrickTable, (nuint)sizeof(uint), out cardBundleOffset) ||
                !TryAdd(cardBundleOffset, cardBundleBytes, out nuint afterCardBundleTable) ||
                !TryAlignUp(afterCardBundleTable, (nuint)sizeof(void*), out segmentMappingOffset) ||
                !TryAdd(segmentMappingOffset, segmentMappingBytes, out allocationSize))
            {
                cardBytes = 0;
                brickBytes = 0;
                cardBundleBytes = 0;
                segmentMappingBytes = 0;
                cardOffset = 0;
                brickOffset = 0;
                cardBundleOffset = 0;
                segmentMappingOffset = 0;
                allocationSize = 0;
                return false;
            }

            return true;
        }

        public static bool IsThreadUsingAllocationContextHeap(IGCHeap* heap, gc_alloc_context* context, int threadNumber)
        {
            _ = heap;
            _ = context;
            _ = threadNumber;
            return true;
        }

        private static nuint ComputeValidSegmentSize(bool largeSegment)
        {
            nuint initialSize = largeSegment ? LargeObjectSegmentSize : InitialSegmentSize;
            nuint segmentSize = (nuint)GCConfig.GetSegmentSize();
            if (largeSegment)
            {
                segmentSize /= 2;
            }

            if (!IsValidSegmentSizeCore(segmentSize))
            {
                segmentSize = ((segmentSize >> 1) != 0 && (segmentSize >> 22) == 0)
                    ? 4 * 1024 * 1024
                    : initialSize;
            }

            return RoundUpPowerOfTwo(segmentSize);
        }

        private static int InitializeHeapState()
        {
            if (s_heapBase is not null)
            {
                return S_OK;
            }

            if (s_minUohSegmentSize > (nuint.MaxValue - s_sohSegmentSize) / 2)
            {
                return E_FAIL;
            }

            nuint totalSize = s_sohSegmentSize + (2 * s_minUohSegmentSize);
            s_heapBase = (byte*)GCToOSInterface.VirtualReserve(totalSize, SegmentAlignment, (uint)VirtualReserveFlags.None);
            if (s_heapBase is null)
            {
                return E_FAIL;
            }

            s_heapEnd = s_heapBase + totalSize;
            if (!GCToOSInterface.VirtualCommit(s_heapBase, SegmentInitialCommit)
                || !GCToOSInterface.VirtualCommit(s_heapBase + s_sohSegmentSize, SegmentInitialCommit)
                || !GCToOSInterface.VirtualCommit(s_heapBase + s_sohSegmentSize + s_minUohSegmentSize, SegmentInitialCommit))
            {
                GCToOSInterface.VirtualRelease(s_heapBase, totalSize);
                s_heapBase = null;
                return E_FAIL;
            }

            if (!s_sohAllocationLock.Initialize())
            {
                GCToOSInterface.VirtualRelease(s_heapBase, totalSize);
                s_heapBase = null;
                return E_FAIL;
            }

            s_sohAllocationLockInitialized = true;
            if (!s_uohAllocationLock.Initialize())
            {
                s_sohAllocationLock.Destroy();
                s_sohAllocationLockInitialized = false;
                GCToOSInterface.VirtualRelease(s_heapBase, totalSize);
                s_heapBase = null;
                return E_FAIL;
            }

            s_uohAllocationLockInitialized = true;
            if (!s_frozenSegmentLock.Initialize())
            {
                s_uohAllocationLock.Destroy();
                s_uohAllocationLockInitialized = false;
                s_sohAllocationLock.Destroy();
                s_sohAllocationLockInitialized = false;
                GCToOSInterface.VirtualRelease(s_heapBase, totalSize);
                s_heapBase = null;
                return E_FAIL;
            }

            s_frozenSegmentLockInitialized = true;

            if (!InitializeFinalizationQueue())
            {
                s_frozenSegmentLock.Destroy();
                s_frozenSegmentLockInitialized = false;
                s_uohAllocationLock.Destroy();
                s_uohAllocationLockInitialized = false;
                s_sohAllocationLock.Destroy();
                s_sohAllocationLockInitialized = false;
                GCToOSInterface.VirtualRelease(s_heapBase, totalSize);
                s_heapBase = null;
                return E_FAIL;
            }

            byte* sohBase = s_heapBase;
            byte* lohBase = sohBase + s_sohSegmentSize;
            byte* pohBase = lohBase + s_minUohSegmentSize;
            fixed (heap_segment* soh = &s_sohSegment)
            fixed (heap_segment* loh = &s_lohSegment)
            fixed (heap_segment* poh = &s_pohSegment)
            fixed (generation* generation0 = &s_generation0)
            fixed (generation* generation1 = &s_generation1)
            fixed (generation* generation2 = &s_generation2)
            fixed (generation* lohGeneration = &s_lohGeneration)
            fixed (generation* pohGeneration = &s_pohGeneration)
            {
                InitializeSegment(soh, sohBase, s_sohSegmentSize);
                InitializeSegment(loh, lohBase, s_minUohSegmentSize);
                InitializeSegment(poh, pohBase, s_minUohSegmentSize);

                byte* sohObjects = sohBase + sizeof(heap_segment);
                byte* generation2Start = sohObjects;
                byte* generation1Start = generation2Start + MinObjectSize;
                byte* generation0Start = generation1Start + MinObjectSize;
                byte* allocationStart = generation0Start + MinObjectSize;
                InitializeGeneration(generation2, 2, soh, generation2Start);
                InitializeGeneration(generation1, 1, soh, generation1Start);
                InitializeGeneration(generation0, 0, soh, generation0Start);
                soh->allocated = allocationStart;
                soh->used = allocationStart;
                FormatUnusedArray(generation2Start, MinObjectSize);
                FormatUnusedArray(generation1Start, MinObjectSize);
                FormatUnusedArray(generation0Start, MinObjectSize);
                generation2->free_obj_space = MinObjectSize;
                generation1->free_obj_space = MinObjectSize;
                generation0->free_obj_space = MinObjectSize;

                InitializeGeneration(lohGeneration, 3, loh, lohBase + sizeof(heap_segment));
                InitializeGeneration(pohGeneration, 4, poh, pohBase + sizeof(heap_segment));
            }

            s_totalAllocatedBytesSoh = 0;
            s_totalAllocatedBytesUoh = 0;

            if (!InitializeCardTable())
            {
                ReleaseFinalizationQueue();
                if (s_sohAllocationLockInitialized)
                {
                    s_sohAllocationLock.Destroy();
                    s_sohAllocationLockInitialized = false;
                }
                if (s_uohAllocationLockInitialized)
                {
                    s_uohAllocationLock.Destroy();
                    s_uohAllocationLockInitialized = false;
                }
                if (s_frozenSegmentLockInitialized)
                {
                    s_frozenSegmentLock.Destroy();
                    s_frozenSegmentLockInitialized = false;
                }
                GCToOSInterface.VirtualRelease(s_heapBase, totalSize);
                s_heapBase = null;
                return E_FAIL;
            }

            WriteBarrierParameters parameters = default;
            parameters.operation = WriteBarrierOp.Initialize;
            parameters.is_runtime_suspended = true;
            parameters.card_table = s_cardTable;
            parameters.card_bundle_table = s_cardBundleTable;
            parameters.lowest_address = s_heapBase;
            parameters.highest_address = s_heapEnd;
            parameters.ephemeral_low = s_generation1.allocation_start;
            parameters.ephemeral_high = s_sohSegment.reserved;
            GCCommon.PublishWriteBarrier(&parameters);

            if (GCCommon.g_theGCToCLR is not null && GCCommon.g_theGCToCLR->Vtable is not null)
            {
                GCCommon.g_theGCToCLR->Vtable->UpdateGCEventStatus(
                    GCCommon.g_theGCToCLR,
                    (int)GCCommon.g_publicEventLevel,
                    (int)GCCommon.g_publicEventKeywords,
                    (int)GCCommon.g_privateEventLevel,
                    (int)GCCommon.g_privateEventKeywords);
            }

            return S_OK;
        }

        public static void SetFinalizationRun(IGCHeap* heap, Object* obj)
        {
            _ = heap;
            obj->GetHeader()->SetBit(ObjHeader.BIT_SBLK_FINALIZER_RUN);
        }

        public static bool RegisterForFinalization(IGCHeap* heap, int generation, Object* obj)
        {
            _ = heap;
            if (generation == -1)
            {
                generation = 0;
            }

            if (obj is null || s_finalizeQueue is null)
            {
                return false;
            }

            ObjHeader* header = obj->GetHeader();
            if ((header->GetBits() & ObjHeader.BIT_SBLK_FINALIZER_RUN) != 0)
            {
                header->ClrBit(ObjHeader.BIT_SBLK_FINALIZER_RUN);
                return true;
            }

            return RegisterForFinalizationCore(generation, obj, 0);
        }

        private static Object* AllocUohObject(gc_alloc_context* context, nuint size, uint flags, bool pinned)
        {
            if (size >= (nuint.MaxValue >> 1) - 7 - MinObjectSize)
            {
                return null;
            }

            s_uohAllocationLock.Enter();

            fixed (heap_segment* lohSegment = &s_lohSegment)
            fixed (heap_segment* pohSegment = &s_pohSegment)
            fixed (generation* lohGeneration = &s_lohGeneration)
            fixed (generation* pohGeneration = &s_pohGeneration)
            {
                heap_segment* segment = pinned ? pohSegment : lohSegment;
                generation* generation = pinned ? pohGeneration : lohGeneration;
                nuint padding = pinned ? 0 : LohPaddingSize;
                if (size > nuint.MaxValue - padding)
                {
                    s_uohAllocationLock.Leave();
                    return null;
                }

                byte* allocation = segment->allocated;
                if (allocation is null ||
                    !TryAlignUp(allocation, (nuint)sizeof(void*), out byte* alignedAllocation) ||
                    alignedAllocation > segment->reserved ||
                    size + padding > (nuint)(segment->reserved - alignedAllocation))
                {
                    s_uohAllocationLock.Leave();
                    return null;
                }

                byte* objectAllocation = alignedAllocation + padding;
                byte* end = objectAllocation + size;
                if (!CommitForAllocation(segment, alignedAllocation, size + padding, segment->mem))
                {
                    s_uohAllocationLock.Leave();
                    return null;
                }

                if (padding != 0)
                {
                    FormatUnusedArray(alignedAllocation, padding);
                    generation->free_obj_space += padding;
                }

                segment->allocated = end;
                if (segment->used < end)
                {
                    segment->used = end;
                }

                generation->end_seg_allocated += size + padding;
                generation->allocation_size += size;
                context->alloc_bytes_uoh += (long)size;
                s_totalAllocatedBytesUoh += size;

                ClearSyncBlock(objectAllocation);
                if ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL) == 0)
                {
                    nuint headerSize = 2 * (nuint)sizeof(void*);
                    if (size > headerSize)
                    {
                        ClearMemory(objectAllocation + headerSize, size - headerSize);
                    }
                }

                s_uohAllocationLock.Leave();
                return (Object*)objectAllocation;
            }
        }

        private static Object* RegisterAllocatedObject(Object* allocation, nuint size, uint flags)
        {
            if (allocation is null ||
                ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_FINALIZE) != 0 &&
                 !RegisterForFinalizationCore(0, allocation, size)))
            {
                return null;
            }

            return allocation;
        }

        private static bool InitializeFinalizationQueue()
        {
            if (s_finalizeQueue is not null)
            {
                return true;
            }

            CFinalize* queue = (CFinalize*)GCToOSInterface.AllocateUnmanaged((nuint)sizeof(CFinalize));
            if (queue is null ||
                !TryMultiply((nuint)InitialFinalizerArraySize, (nuint)sizeof(void*), out nuint arraySize))
            {
                if (queue is not null)
                {
                    GCToOSInterface.FreeUnmanaged(queue);
                }

                return false;
            }

            Object** array = (Object**)GCToOSInterface.AllocateUnmanaged(arraySize);
            if (array is null)
            {
                GCToOSInterface.FreeUnmanaged(queue);
                return false;
            }

            *queue = default;
            queue->m_Array = array;
            queue->m_EndArray = array + InitialFinalizerArraySize;
            for (int segment = 0; segment < FinalizerFreeListSegment; segment++)
            {
                SetFillPointer(queue, segment, array);
            }

            queue->m_PromotedCount = 0;
            queue->lock_value = -1;
            queue->lockowner_threadid.Clear();
            s_finalizeQueue = queue;
            return true;
        }

        private static void ReleaseFinalizationQueue()
        {
            if (s_finalizeQueue is null)
            {
                return;
            }

            GCToOSInterface.FreeUnmanaged(s_finalizeQueue->m_Array);
            GCToOSInterface.FreeUnmanaged(s_finalizeQueue);
            s_finalizeQueue = null;
        }

        private static bool RegisterForFinalizationCore(int generation, Object* obj, nuint size)
        {
            if (generation < 0 || generation > (int)gc_generation_num.max_generation)
            {
                return false;
            }

            CFinalize* queue = s_finalizeQueue;
            if (queue is null)
            {
                return false;
            }

            EnterFinalizeLock(queue);

            int destination = (int)gc_generation_num.total_generation_count - generation - 1;
            Object** freeListStart = GetQueueStart(queue, FinalizerFreeListSegment);
            if (freeListStart == queue->m_EndArray && !GrowFinalizationArray(queue))
            {
                LeaveFinalizeLock(queue);
                if (obj->RawGetMethodTable() is null)
                {
                    FormatUnusedArray((byte*)obj, size);
                }

                return false;
            }

            Object*** fillPointers = &queue->m_FillPointers.Item0;
            Object*** currentFillPointer = &fillPointers[FinalizerFreeListSegment - 1];
            Object*** destinationEnd = &fillPointers[destination];
            do
            {
                if (*currentFillPointer != *(currentFillPointer - 1))
                {
                    *(*currentFillPointer) = *(*(currentFillPointer - 1));
                }

                (*currentFillPointer)++;
                currentFillPointer--;
            }
            while (currentFillPointer > destinationEnd);

            **currentFillPointer = obj;
            (*currentFillPointer)++;
            LeaveFinalizeLock(queue);
            return true;
        }

        private static void EnterFinalizeLock(CFinalize* queue)
        {
            int* lockPointer = &queue->lock_value;
            while (Interlocked.CompareExchange(ref *lockPointer, 0, -1) >= 0)
            {
                uint iteration = 0;
                while (System.Threading.Volatile.Read(ref *lockPointer) >= 0)
                {
                    if (GCCommon.g_num_processors > 1)
                    {
                        long configuredSpinCount = GCConfig.GetGCSpinCountUnit();
                        int spinCountUnit = configuredSpinCount > 0
                            ? unchecked((int)configuredSpinCount)
                            : unchecked((int)(32u * GCCommon.g_num_processors));
                        int spinCount = unchecked(128 * spinCountUnit);
                        for (int i = 0; i < spinCount; i++)
                        {
                            if (System.Threading.Volatile.Read(ref *lockPointer) < 0)
                            {
                                break;
                            }

                            GCToOSInterface.YieldProcessor();
                        }
                    }

                    if (System.Threading.Volatile.Read(ref *lockPointer) < 0)
                    {
                        break;
                    }

                    iteration++;
                    if ((iteration & 7) != 0)
                    {
                        GCToOSInterface.YieldThread(0);
                    }
                    else
                    {
                        GCToOSInterface.Sleep(5);
                    }
                }
            }

            queue->lockowner_threadid.SetToCurrentThread();
        }

        private static void LeaveFinalizeLock(CFinalize* queue)
        {
            queue->lockowner_threadid.Clear();
            System.Threading.Volatile.Write(ref queue->lock_value, -1);
        }

        private static bool GrowFinalizationArray(CFinalize* queue)
        {
            nuint oldArraySize = (nuint)(queue->m_EndArray - queue->m_Array);
            nuint newArraySize = unchecked((oldArraySize * 12) / 10);
            if (!TryMultiply(newArraySize, (nuint)sizeof(void*), out nuint arraySize))
            {
                return false;
            }

            Object** oldArray = queue->m_Array;
            Object** newArray = (Object**)GCToOSInterface.AllocateUnmanaged(arraySize);
            if (newArray is null)
            {
                return false;
            }

            for (nuint i = 0; i < oldArraySize; i++)
            {
                newArray[i] = oldArray[i];
            }

            for (int segment = 0; segment < FinalizerFreeListSegment; segment++)
            {
                Object** fillPointer = GetFillPointer(queue, segment);
                SetFillPointer(queue, segment, newArray + (fillPointer - oldArray));
            }

            queue->m_Array = newArray;
            queue->m_EndArray = newArray + newArraySize;
            GCToOSInterface.FreeUnmanaged(oldArray);
            return true;
        }

        private static Object** GetFillPointer(CFinalize* queue, int index)
        {
            Object*** fillPointers = &queue->m_FillPointers.Item0;
            return fillPointers[index];
        }

        private static void SetFillPointer(CFinalize* queue, int index, Object** value)
        {
            Object*** fillPointers = &queue->m_FillPointers.Item0;
            fillPointers[index] = value;
        }

        private static Object** GetQueueStart(CFinalize* queue, int segment)
        {
            return segment == 0 ? queue->m_Array : GetFillPointer(queue, segment - 1);
        }

        private static Object** GetQueueLimit(CFinalize* queue, int segment)
        {
            return segment == FinalizerFreeListSegment ? queue->m_EndArray : GetFillPointer(queue, segment);
        }

        private static void SetQueueStart(CFinalize* queue, int segment, Object** value)
        {
            SetFillPointer(queue, segment - 1, value);
        }

        private static void SetQueueLimit(CFinalize* queue, int segment, Object** value)
        {
            if (segment == FinalizerFreeListSegment)
            {
                queue->m_EndArray = value;
            }
            else
            {
                SetFillPointer(queue, segment, value);
            }
        }

        private static void ClearMemory(byte* memory, nuint size)
        {
            for (nuint i = 0; i < size; i++)
            {
                memory[i] = 0;
            }
        }

        private static void ClearSyncBlock(byte* allocation)
        {
            *((nuint*)(allocation - sizeof(void*))) = 0;
        }

        private static uint GetGenerationWithRangeCore(Object* obj, byte** start, byte** allocated, byte** reserved)
        {
            if (obj is null)
            {
                return int.MaxValue;
            }

            nuint address = (nuint)obj;
            if (address < (nuint)s_heapBase || address >= (nuint)s_heapEnd || IsInFrozenSegment(null, obj))
            {
                return int.MaxValue;
            }

            if (IsAddressInSegment(address, s_lohSegment))
            {
                SetGenerationRange(start, allocated, reserved, s_lohSegment.mem, s_lohSegment.allocated, s_lohSegment.reserved);
                return (uint)gc_generation_num.loh_generation;
            }

            if (IsAddressInSegment(address, s_pohSegment))
            {
                SetGenerationRange(start, allocated, reserved, s_pohSegment.mem, s_pohSegment.allocated, s_pohSegment.reserved);
                return (uint)gc_generation_num.poh_generation;
            }

            if (!IsAddressInSegment(address, s_sohSegment))
            {
                return int.MaxValue;
            }

            byte* end = s_sohSegment.allocated;
            byte* rangeReserved = s_sohSegment.reserved;
            for (int generation = 0; generation < (int)gc_generation_num.max_generation; generation++)
            {
                byte* generationStart = GetGenerationAllocationStart(generation);
                if ((byte*)obj >= generationStart)
                {
                    SetGenerationRange(start, allocated, reserved, generationStart, end, rangeReserved);
                    return (uint)generation;
                }

                end = rangeReserved = generationStart;
            }

            SetGenerationRange(start, allocated, reserved, s_sohSegment.mem, end, rangeReserved);
            return (uint)gc_generation_num.max_generation;
        }

        private static byte* GetGenerationAllocationStart(int generation)
        {
            return generation switch
            {
                0 => s_generation0.allocation_start,
                1 => s_generation1.allocation_start,
                _ => s_generation2.allocation_start,
            };
        }

        private static void SetGenerationRange(byte** start, byte** allocated, byte** reserved, byte* rangeStart, byte* rangeAllocated, byte* rangeReserved)
        {
            if (start is not null)
            {
                *start = rangeStart;
            }

            if (allocated is not null)
            {
                *allocated = rangeAllocated;
            }

            if (reserved is not null)
            {
                *reserved = rangeReserved;
            }
        }

        private static nuint GetSegmentBytesInUse(heap_segment* segment, nuint freeObjectSpace, nuint freeListSpace)
        {
            if (segment->allocated <= segment->mem)
            {
                return 0;
            }

            nuint size = (nuint)(segment->allocated - segment->mem);
            nuint free = freeObjectSpace + freeListSpace;

            return free < size ? size - free : 0;
        }

        private static void InitializeSegment(heap_segment* segment, byte* baseAddress, nuint size)
        {
            *segment = default;
            segment->mem = baseAddress + sizeof(heap_segment);
            segment->reserved = baseAddress + size;
            segment->committed = baseAddress + SegmentInitialCommit;
            segment->allocated = segment->mem;
            segment->used = segment->mem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct frozen_segment_entry
        {
            public heap_segment* segment;
            public frozen_segment_entry* next;
        }

        private static bool TryAddPointer(byte* address, nuint offset, out byte* result)
        {
            nuint value = (nuint)address;
            if (offset > nuint.MaxValue - value)
            {
                result = null;
                return false;
            }

            result = (byte*)(value + offset);
            return true;
        }

        private static bool IsAddressInSegment(nuint address, heap_segment segment)
        {
            return segment.mem is not null &&
                address >= (nuint)segment.mem &&
                address < (nuint)segment.reserved;
        }

        private static frozen_segment_entry* FindFrozenSegmentByBase(byte* memory)
        {
            frozen_segment_entry* entry = s_frozenSegmentLookup;
            while (entry is not null)
            {
                if (entry->segment->mem == memory)
                {
                    return entry;
                }

                if ((nuint)entry->segment->mem > (nuint)memory)
                {
                    break;
                }

                entry = entry->next;
            }

            return null;
        }

        private static frozen_segment_entry* FindFrozenSegmentByHandle(heap_segment* segment)
        {
            frozen_segment_entry* entry = s_frozenSegmentLookup;
            while (entry is not null)
            {
                if (entry->segment == segment)
                {
                    return entry;
                }

                entry = entry->next;
            }

            return null;
        }

        private static frozen_segment_entry* FindFrozenSegmentContaining(nuint address)
        {
            frozen_segment_entry* entry = s_frozenSegmentLookup;
            while (entry is not null)
            {
                nuint memory = (nuint)entry->segment->mem;
                if (address < memory)
                {
                    break;
                }

                if (address < (nuint)entry->segment->reserved)
                {
                    return entry;
                }

                entry = entry->next;
            }

            return null;
        }

        private static void InsertFrozenSegmentLookup(frozen_segment_entry* entry)
        {
            if (s_frozenSegmentLookup is null ||
                (nuint)entry->segment->mem < (nuint)s_frozenSegmentLookup->segment->mem)
            {
                entry->next = s_frozenSegmentLookup;
                s_frozenSegmentLookup = entry;
                return;
            }

            frozen_segment_entry* previous = s_frozenSegmentLookup;
            while (previous->next is not null &&
                   (nuint)previous->next->segment->mem < (nuint)entry->segment->mem)
            {
                previous = previous->next;
            }

            entry->next = previous->next;
            previous->next = entry;
        }

        private static void RemoveFrozenSegmentLookup(frozen_segment_entry* entry)
        {
            if (s_frozenSegmentLookup == entry)
            {
                s_frozenSegmentLookup = entry->next;
                return;
            }

            frozen_segment_entry* previous = s_frozenSegmentLookup;
            while (previous is not null && previous->next != entry)
            {
                previous = previous->next;
            }

            if (previous is not null)
            {
                previous->next = entry->next;
            }
        }

        private static void RemoveFrozenSegmentFromGeneration(heap_segment* segment)
        {
            heap_segment* previous = null;
            heap_segment* current = s_generation2.start_segment;
            while (current is not null && current != segment)
            {
                previous = current;
                current = current->next;
            }

            if (current is null)
            {
                return;
            }

            if (previous is null)
            {
                s_generation2.start_segment = current->next;
            }
            else
            {
                previous->next = current->next;
            }
        }

        private static void ReleaseFrozenSegments()
        {
            frozen_segment_entry* entry = s_frozenSegmentLookup;
            while (entry is not null)
            {
                frozen_segment_entry* next = entry->next;
                GCToOSInterface.FreeUnmanaged(entry->segment);
                GCToOSInterface.FreeUnmanaged(entry);
                entry = next;
            }

            s_frozenSegmentLookup = null;
            fixed (heap_segment* soh = &s_sohSegment)
            {
                s_generation2.start_segment = soh;
            }
        }

        private static void InitializeGeneration(generation* generation, int number, heap_segment* segment, byte* start)
        {
            *generation = default;
            generation->gen_num = number;
            generation->start_segment = segment;
            generation->allocation_start = start;
            generation->allocation_segment = segment;
            generation->allocation_context_start_region = null;
        }

        private static bool InitializeCardTable()
        {
            if (!TryGetBookkeepingSize(
                    s_heapBase,
                    s_heapEnd,
                    out nuint cardBytes,
                    out nuint brickBytes,
                    out nuint cardBundleBytes,
                    out nuint segmentMappingBytes,
                    out nuint cardOffset,
                    out nuint brickOffset,
                    out nuint cardBundleOffset,
                    out nuint segmentMappingOffset,
                    out nuint allocationSize))
            {
                return false;
            }

            byte* bookkeeping = (byte*)GCToOSInterface.VirtualReserve(allocationSize, 0, (uint)VirtualReserveFlags.None);
            if (bookkeeping is null)
            {
                return false;
            }

            if (!GCToOSInterface.VirtualCommit(bookkeeping, allocationSize))
            {
                GCToOSInterface.VirtualRelease(bookkeeping, allocationSize);
                return false;
            }

            uint* untranslatedCardTable = (uint*)(bookkeeping + cardOffset);
            uint* untranslatedCardBundleTable = (uint*)(bookkeeping + cardBundleOffset);
            CardTableInfo* info = (CardTableInfo*)bookkeeping;
            info->recount = 0;
            info->size = allocationSize;
            info->next_card_table = null;
            info->lowest_address = s_heapBase;
            info->highest_address = s_heapEnd;
            info->brick_table = (short*)(bookkeeping + brickOffset);
            info->card_bundle_table = untranslatedCardBundleTable;
            s_cardTable = (uint*)((byte*)untranslatedCardTable - ((((nuint)s_heapBase / CardSize) / CardWordWidth) * sizeof(uint)));
            s_cardBundleTable = (uint*)((byte*)untranslatedCardBundleTable -
                ((((nuint)s_heapBase / CardBundleWordCoverage) * sizeof(uint))));
            _ = brickBytes;
            _ = cardBundleBytes;
            _ = segmentMappingBytes;
            return true;
        }

        private static bool CommitForAllocation(heap_segment* segment, byte* allocation, nuint size, byte* minimumAllocation)
        {
            if (allocation < minimumAllocation || allocation > segment->reserved)
            {
                return false;
            }

            if (size > (nuint)(segment->reserved - allocation))
            {
                return false;
            }

            byte* requiredEnd = allocation + size;
            if (!TryAlignUp(requiredEnd, GCToOSInterface.PageSize, out byte* commitEnd) ||
                commitEnd > segment->reserved)
            {
                return false;
            }

            if (commitEnd <= segment->committed)
            {
                return true;
            }

            nuint commitSize = (nuint)(commitEnd - segment->committed);
            bool committed = GCToOSInterface.VirtualCommit(segment->committed, commitSize);
            if (!committed)
            {
                return false;
            }

            segment->committed = commitEnd;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CardTableInfo
        {
            public uint recount;
            public nuint size;
            public uint* next_card_table;
            public byte* lowest_address;
            public byte* highest_address;
            public short* brick_table;
            public uint* card_bundle_table;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SegMapping
        {
            public byte* boundary;
            public heap_segment* seg0;
            public heap_segment* seg1;
        }

        private static nuint RoundUpPowerOfTwo(nuint value)
        {
            if (value <= 1)
            {
                return 1;
            }

            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            if (sizeof(nuint) == 8)
            {
                value |= value >> 32;
            }

            return value + 1;
        }

        private static bool IsValidSegmentSizeCore(nuint size)
        {
            return (size & (1024 * 1024 - 1)) == 0 && size >= 4 * 1024 * 1024;
        }
    }
}
