// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The managed implementation of <c>gcinterface.h</c>'s <c>IGCHeap</c>: a heap that
    /// allocates but never collects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the first heap of the port, and it exists to prove the bootstrapping story —
    /// that ILC can compile code the runtime is willing to call on the allocation path and with
    /// the world suspended — before any of the collector is written. Allocation is a bump
    /// pointer over <see cref="GCHeapMemory"/>; every method that would need marking, planning,
    /// sweeping or a walkable heap either reports "nothing to do" or stops the process through
    /// <see cref="Unsupported"/> rather than returning a plausible-looking wrong answer.
    /// </para>
    /// <para>
    /// The object handed to the EE is C++-shaped: a pointer to a word that holds the address of
    /// the vtable. Both live in non-GC statics, which ILC emits into the image's data section at
    /// fixed addresses, so they are valid from the moment <c>Create</c> runs — which matters
    /// because <c>GC_Initialize</c> runs before there is a heap to allocate them from.
    /// </para>
    /// </remarks>
    internal static unsafe class ManagedGCHeap
    {
        internal const uint MaxGeneration = 2;

        private const int S_OK = 0;
        private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

        /// <summary>
        /// Objects this size and above bypass the allocation context. Must match
        /// <c>RH_LARGE_OBJECT_SIZE</c>, which the EE compares against
        /// <see cref="GetLOHThreshold"/> when deciding to set
        /// <see cref="GC_ALLOC_FLAGS.GC_ALLOC_LARGE_OBJECT_HEAP"/>.
        /// </summary>
        private const nuint LargeObjectSize = 85000;

        /// <summary>
        /// How much a thread's allocation context is refilled with. Must stay below
        /// <see cref="LargeObjectSize"/>: <c>GcAllocInternal</c> asserts that an allocation
        /// context never spans more than that.
        /// </summary>
        private const nuint AllocQuantum = 8 * 1024;

        /// <summary>Frozen segments the EE can register. One per module, plus the frozen object heap.</summary>
        private const int MaxFrozenSegments = 64;

        private static IGCHeapInternalVtable s_vtable;
        private static nint s_vtablePtr;

        private static long s_totalAllocatedBytes;
        private static int s_gcCount;
        private static int s_gcInProgress;

        private static FrozenSegment* s_frozenSegments;
        private static int s_frozenSegmentCount;

        /// <summary>
        /// A range registered through <see cref="RegisterFrozenSegment"/>. These hold objects
        /// the EE created outside the heap — module frozen object regions and the frozen object
        /// heap — so they are never allocated from and never reclaimed.
        /// </summary>
        private struct FrozenSegment
        {
            // Held as nint rather than byte* so that they can be read and written atomically;
            // Volatile and Interlocked cannot be instantiated over a pointer type.
            public nint Start;
            public nint End;
        }

        /// <summary>
        /// Builds the vtable and returns the <c>IGCHeap*</c> to hand to the EE. The object is an
        /// <c>IGCHeapInternal</c>, as <c>GCHeap</c> is in the C++ GC; the EE only reads the
        /// <c>IGCHeap</c> prefix of the vtable.
        /// </summary>
        public static void* Create()
        {
            // Start from a table that is entirely fail-fast so that a slot which is not filled
            // in below stops at the call rather than being a null dereference. The signatures
            // do not match, which is harmless because the target never returns; the
            // architectures this GC supports all clean up arguments in the caller.
            void** slots = (void**)Unsafe.AsPointer(ref s_vtable);
            for (int i = 0; i < IGCHeapInternalVtable.SlotCount; i++)
            {
                slots[i] = (void*)(delegate*<void>)&Unsupported;
            }

            s_vtable.IGCHeap.IsValidSegmentSize = &IsValidSegmentSize;
            s_vtable.IGCHeap.IsValidGen0MaxSize = &IsValidGen0MaxSize;
            s_vtable.IGCHeap.GetValidSegmentSize = &GetValidSegmentSize;
            s_vtable.IGCHeap.SetReservedVMLimit = &SetReservedVMLimit;
            s_vtable.IGCHeap.WaitUntilConcurrentGCComplete = &WaitUntilConcurrentGCComplete;
            s_vtable.IGCHeap.IsConcurrentGCInProgress = &IsConcurrentGCInProgress;
            s_vtable.IGCHeap.TemporaryEnableConcurrentGC = &TemporaryEnableConcurrentGC;
            s_vtable.IGCHeap.TemporaryDisableConcurrentGC = &TemporaryDisableConcurrentGC;
            s_vtable.IGCHeap.IsConcurrentGCEnabled = &IsConcurrentGCEnabled;
            s_vtable.IGCHeap.WaitUntilConcurrentGCCompleteAsync = &WaitUntilConcurrentGCCompleteAsync;
            s_vtable.IGCHeap.GetNumberOfFinalizable = &GetNumberOfFinalizable;
            s_vtable.IGCHeap.GetNextFinalizable = &GetNextFinalizable;
            s_vtable.IGCHeap.GetMemoryInfo = &GetMemoryInfo;
            s_vtable.IGCHeap.GetMemoryLoad = &GetMemoryLoad;
            s_vtable.IGCHeap.GetGcLatencyMode = &GetGcLatencyMode;
            s_vtable.IGCHeap.SetGcLatencyMode = &SetGcLatencyMode;
            s_vtable.IGCHeap.GetLOHCompactionMode = &GetLOHCompactionMode;
            s_vtable.IGCHeap.SetLOHCompactionMode = &SetLOHCompactionMode;
            s_vtable.IGCHeap.RegisterForFullGCNotification = &RegisterForFullGCNotification;
            s_vtable.IGCHeap.CancelFullGCNotification = &CancelFullGCNotification;
            s_vtable.IGCHeap.WaitForFullGCApproach = &WaitForFullGCApproach;
            s_vtable.IGCHeap.WaitForFullGCComplete = &WaitForFullGCComplete;
            s_vtable.IGCHeap.WhichGeneration = &WhichGeneration;
            s_vtable.IGCHeap.CollectionCount = &CollectionCount;
            s_vtable.IGCHeap.StartNoGCRegion = &StartNoGCRegion;
            s_vtable.IGCHeap.EndNoGCRegion = &EndNoGCRegion;
            s_vtable.IGCHeap.GetTotalBytesInUse = &GetTotalBytesInUse;
            s_vtable.IGCHeap.GetTotalAllocatedBytes = &GetTotalAllocatedBytes;
            s_vtable.IGCHeap.GarbageCollect = &GarbageCollect;
            s_vtable.IGCHeap.GetMaxGeneration = &GetMaxGeneration;
            s_vtable.IGCHeap.SetFinalizationRun = &SetFinalizationRun;
            s_vtable.IGCHeap.RegisterForFinalization = &RegisterForFinalization;
            s_vtable.IGCHeap.GetLastGCPercentTimeInGC = &GetLastGCPercentTimeInGC;
            s_vtable.IGCHeap.GetLastGCGenerationSize = &GetLastGCGenerationSize;
            s_vtable.IGCHeap.Initialize = &Initialize;
            s_vtable.IGCHeap.IsPromoted = &IsPromoted;
            s_vtable.IGCHeap.IsHeapPointer = &IsHeapPointer;
            s_vtable.IGCHeap.GetCondemnedGeneration = &GetCondemnedGeneration;
            s_vtable.IGCHeap.IsGCInProgressHelper = &IsGCInProgressHelper;
            s_vtable.IGCHeap.GetGcCount = &GetGcCount;
            s_vtable.IGCHeap.IsThreadUsingAllocationContextHeap = &IsThreadUsingAllocationContextHeap;
            s_vtable.IGCHeap.IsEphemeral = &IsEphemeral;
            s_vtable.IGCHeap.WaitUntilGCComplete = &WaitUntilGCComplete;
            s_vtable.IGCHeap.FixAllocContext = &FixAllocContext;
            s_vtable.IGCHeap.GetCurrentObjSize = &GetCurrentObjSize;
            s_vtable.IGCHeap.SetGCInProgress = &SetGCInProgress;
            s_vtable.IGCHeap.RuntimeStructuresValid = &RuntimeStructuresValid;
            s_vtable.IGCHeap.SetSuspensionPending = &SetSuspensionPending;
            s_vtable.IGCHeap.SetYieldProcessorScalingFactor = &SetYieldProcessorScalingFactor;
            s_vtable.IGCHeap.Shutdown = &Shutdown;
            s_vtable.IGCHeap.GetLastGCStartTime = &GetLastGCStartTime;
            s_vtable.IGCHeap.GetLastGCDuration = &GetLastGCDuration;
            s_vtable.IGCHeap.GetNow = &GetNow;
            s_vtable.IGCHeap.Alloc = &Alloc;
            s_vtable.IGCHeap.PublishObject = &PublishObject;
            s_vtable.IGCHeap.SetWaitForGCEvent = &SetWaitForGCEvent;
            s_vtable.IGCHeap.ResetWaitForGCEvent = &ResetWaitForGCEvent;
            s_vtable.IGCHeap.IsLargeObject = &IsLargeObject;
            s_vtable.IGCHeap.ValidateObjectMember = &ValidateObjectMember;
            s_vtable.IGCHeap.DiagScanFinalizeQueue = &DiagScanFinalizeQueue;
            s_vtable.IGCHeap.DiagScanHandles = &DiagScanHandles;
            s_vtable.IGCHeap.DiagScanDependentHandles = &DiagScanDependentHandles;
            s_vtable.IGCHeap.DiagDescrGenerations = &DiagDescrGenerations;
            s_vtable.IGCHeap.DiagTraceGCSegments = &DiagTraceGCSegments;
            s_vtable.IGCHeap.DiagGetGCSettings = &DiagGetGCSettings;
            s_vtable.IGCHeap.StressHeap = &StressHeap;
            s_vtable.IGCHeap.RegisterFrozenSegment = &RegisterFrozenSegment;
            s_vtable.IGCHeap.UnregisterFrozenSegment = &UnregisterFrozenSegment;
            s_vtable.IGCHeap.IsInFrozenSegment = &IsInFrozenSegment;
            s_vtable.IGCHeap.ControlEvents = &ControlEvents;
            s_vtable.IGCHeap.ControlPrivateEvents = &ControlPrivateEvents;
            s_vtable.IGCHeap.GetGenerationWithRange = &GetGenerationWithRange;
            s_vtable.IGCHeap.GetTotalPauseDuration = &GetTotalPauseDuration;
            s_vtable.IGCHeap.EnumerateConfigurationValues = &EnumerateConfigurationValues;
            s_vtable.IGCHeap.UpdateFrozenSegment = &UpdateFrozenSegment;
            s_vtable.IGCHeap.RefreshMemoryLimit = &RefreshMemoryLimit;
            s_vtable.IGCHeap.EnableNoGCRegionCallback = &EnableNoGCRegionCallback;
            s_vtable.IGCHeap.GetExtraWorkForFinalization = &GetExtraWorkForFinalization;
            s_vtable.IGCHeap.GetGenerationBudget = &GetGenerationBudget;
            s_vtable.IGCHeap.GetLOHThreshold = &GetLOHThreshold;
            s_vtable.IGCHeap.NullBridgeObjectsWeakRefs = &NullBridgeObjectsWeakRefs;

            s_vtable.GetNumberOfHeaps = &GetNumberOfHeaps;
            s_vtable.GetHomeHeapNumber = &GetHomeHeapNumber;
            s_vtable.GetPromotedBytes = &GetPromotedBytes;
            s_vtable.IsPromoted2 = &IsPromoted2;

            s_vtablePtr = (nint)Unsafe.AsPointer(ref s_vtable);
            return Unsafe.AsPointer(ref s_vtablePtr);
        }

        [RuntimeImport("*", "ManagedGC_Unsupported")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void FailFastUnsupported();

        /// <summary>
        /// Stands in for every <c>IGCHeap</c> method this heap cannot answer. These are the
        /// methods that need a collector or a walkable heap; reaching one means something asked
        /// this GC to do real work, which is a bug in the caller's expectations rather than
        /// something to paper over.
        /// </summary>
        private static void Unsupported() => FailFastUnsupported();

        // ------------------------------------------------------------------------------------
        // Startup
        // ------------------------------------------------------------------------------------

        private static int Initialize(void* thisPtr)
        {
#if BACKGROUND_GC
            GCCommon.initialize();
#endif

            if (!gc_heap.check_commit_cs.Initialize())
            {
                return E_OUTOFMEMORY;
            }

            if (!GCHeapMemory.Initialize())
            {
                gc_heap.check_commit_cs.Destroy();
                return E_OUTOFMEMORY;
            }

            // The frozen segment table is carved out of the heap rather than kept in a static
            // because a static would have to be a fixed-size buffer; the heap is available by
            // now and nothing it hands out is ever reclaimed, so the table is equally stable.
            s_frozenSegments = (FrozenSegment*)GCHeapMemory.Allocate((nuint)(sizeof(FrozenSegment) * MaxFrozenSegments));
            return s_frozenSegments != null ? S_OK : E_OUTOFMEMORY;
        }

        private static void Shutdown(void* thisPtr)
        {
        }

        // ------------------------------------------------------------------------------------
        // Allocation
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Port of <c>IGCHeap::Alloc</c>. Returns zeroed memory for the object, or null when
        /// the heap is exhausted; the caller sets the method table and the array length.
        /// </summary>
        /// <remarks>
        /// <c>GC_ALLOC_ALIGN8</c> and <c>GC_ALLOC_ALIGN8_BIAS</c> are not honored, so only
        /// targets that do not build with <c>FEATURE_64BIT_ALIGNMENT</c> are supported.
        /// <c>Microsoft.NETCore.Native.targets</c> rejects the others.
        /// </remarks>
        private static byte* Alloc(void* thisPtr, gc_alloc_context* acontext, nuint size, uint flags)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            byte* result = AllocCore(acontext, size, flags);
            criticalRegion.Exit();
            return result;
        }

        private static byte* AllocCore(gc_alloc_context* acontext, nuint size, uint flags)
        {
            size = (size + (nuint)sizeof(nuint) - 1) & ~((nuint)sizeof(nuint) - 1);

            // Large objects are handed out whole rather than from the allocation context, both
            // because they would not fit and because the EE expects a context to stay small.
            if ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_LARGE_OBJECT_HEAP) != 0 || size >= LargeObjectSize)
            {
                byte* uoh = GCHeapMemory.Allocate(size);
                if (uoh != null)
                {
                    acontext->alloc_bytes_uoh += (long)size;
                    Interlocked.ExchangeAdd64(ref s_totalAllocatedBytes, (long)size);
                }

                return uoh;
            }

            byte* allocPtr = acontext->alloc_ptr;
            if (allocPtr != null && size <= (nuint)(acontext->alloc_limit - allocPtr))
            {
                acontext->alloc_ptr = allocPtr + size;
                return allocPtr;
            }

            // Whatever is left in the old context is abandoned. Nothing walks this heap and
            // nothing reclaims it, so no free object needs to be written over the gap.
            nuint quantum = size > AllocQuantum ? size : AllocQuantum;
            byte* region = GCHeapMemory.Allocate(quantum);
            if (region == null)
            {
                return null;
            }

            acontext->alloc_ptr = region + size;
            acontext->alloc_limit = region + quantum;
            acontext->alloc_bytes += (long)quantum;
            acontext->alloc_count++;
            Interlocked.ExchangeAdd64(ref s_totalAllocatedBytes, (long)quantum);
            return region;
        }

        /// <summary>
        /// Retires a thread's allocation context. The unused tail is abandoned rather than
        /// returned to the heap, which is consistent with never reclaiming anything.
        /// </summary>
        private static void FixAllocContext(void* thisPtr, gc_alloc_context* acontext, void* arg, void* heap)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            acontext->alloc_ptr = null;
            acontext->alloc_limit = null;
            criticalRegion.Exit();
        }

        private static void PublishObject(void* thisPtr, byte* obj)
        {
        }

        private static nuint GetLOHThreshold(void* thisPtr) => LargeObjectSize;

        private static byte IsLargeObject(void* thisPtr, byte* obj) => 0;

        private static byte IsThreadUsingAllocationContextHeap(void* thisPtr, gc_alloc_context* acontext, int thread_number) => 1;

        /// <summary>
        /// The <c>IGCHeapInternal</c> heap-count slots. This is a single-heap workstation-shaped
        /// heap, so there is one heap and every thread's home heap is heap 0.
        /// </summary>
        private static int GetNumberOfHeaps(void* thisPtr) => 1;

        private static int GetHomeHeapNumber(void* thisPtr) => 0;

        /// <summary>Nothing is ever marked, so nothing has ever been promoted by a collection.</summary>
        private static nuint GetPromotedBytes(void* thisPtr, int heap_index) => 0;

        private static nuint GetCurrentObjSize(void* thisPtr) => 0;

        // ------------------------------------------------------------------------------------
        // Collection - there isn't one
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Port of <c>IGCHeap::GarbageCollect</c>. The EE is suspended and restarted so the
        /// managed heap exercises the real stop-the-world protocol, but nothing is reclaimed.
        /// </summary>
        private static int GarbageCollect(void* thisPtr, int generation, byte low_memory_p, int mode)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
            Interlocked.Increment(ref s_gcCount);
            GCToEEInterface.RestartEE(1);
            criticalRegion.Exit();
            return S_OK;
        }

        private static uint GetMaxGeneration(void* thisPtr) => MaxGeneration;

        private static uint GetCondemnedGeneration(void* thisPtr) => 0;

        private static int CollectionCount(void* thisPtr, int generation, int get_bgc_fgc_count) => Volatile.Read(ref s_gcCount);

        private static uint GetGcCount(void* thisPtr) => (uint)Volatile.Read(ref s_gcCount);

        private static byte IsGCInProgressHelper(void* thisPtr, byte bConsiderGCStart) =>
            Volatile.Read(ref s_gcInProgress) != 0 ? (byte)1 : (byte)0;

        private static void SetGCInProgress(void* thisPtr, byte fInProgress)
        {
            Volatile.Write(ref s_gcInProgress, fInProgress);
        }

        private static uint WaitUntilGCComplete(void* thisPtr, byte bConsiderGCStart) => 0;

        private static void SetWaitForGCEvent(void* thisPtr)
        {
        }

        private static void ResetWaitForGCEvent(void* thisPtr)
        {
        }

        private static void SetSuspensionPending(void* thisPtr, byte fSuspensionPending)
        {
        }

        private static void SetYieldProcessorScalingFactor(void* thisPtr, float yieldProcessorScalingFactor)
        {
        }

        private static byte RuntimeStructuresValid(void* thisPtr) => 1;

        private static byte StressHeap(void* thisPtr, gc_alloc_context* acontext) => 0;

        private static void ValidateObjectMember(void* thisPtr, byte* obj)
        {
        }

        // ------------------------------------------------------------------------------------
        // Concurrent GC - there isn't one
        // ------------------------------------------------------------------------------------

        private static void WaitUntilConcurrentGCComplete(void* thisPtr)
        {
        }

        private static int WaitUntilConcurrentGCCompleteAsync(void* thisPtr, int millisecondsTimeout) => S_OK;

        private static byte IsConcurrentGCInProgress(void* thisPtr) => 0;

        private static byte IsConcurrentGCEnabled(void* thisPtr) => 0;

        private static void TemporaryEnableConcurrentGC(void* thisPtr)
        {
        }

        private static void TemporaryDisableConcurrentGC(void* thisPtr)
        {
        }

        // ------------------------------------------------------------------------------------
        // Finalization - nothing is ever finalized, because nothing ever dies
        // ------------------------------------------------------------------------------------

        private static nuint GetNumberOfFinalizable(void* thisPtr) => 0;

        private static byte* GetNextFinalizable(void* thisPtr) => null;

        private static void SetFinalizationRun(void* thisPtr, byte* obj)
        {
        }

        /// <summary>
        /// Port of <c>IGCHeap::RegisterForFinalization</c>. Reports success without recording
        /// anything: returning false makes the EE throw <c>OutOfMemoryException</c> from every
        /// allocation of a finalizable type, which would be a far more confusing failure than
        /// finalizers simply never running.
        /// </summary>
        private static byte RegisterForFinalization(void* thisPtr, int gen, byte* obj) => 1;

        private static FinalizerWorkItem* GetExtraWorkForFinalization(void* thisPtr) => null;

        private static void NullBridgeObjectsWeakRefs(void* thisPtr, nuint length, void* unreachableObjectHandles)
        {
        }

        // ------------------------------------------------------------------------------------
        // Object inspection
        // ------------------------------------------------------------------------------------

        private static byte IsPromoted(void* thisPtr, byte* obj) => 1;

        /// <summary>
        /// The <c>IGCHeapInternal</c> form of <see cref="IsPromoted"/>, used by the bridge code.
        /// Nothing is ever collected, so everything is promoted, and there is no next header to
        /// verify.
        /// </summary>
        private static byte IsPromoted2(void* thisPtr, byte* obj, byte bVerifyNextHeader) => 1;

        private static byte IsHeapPointer(void* thisPtr, void* obj, byte small_heap_only) =>
            GCHeapMemory.Contains(obj) || FindFrozenSegment((byte*)obj) != null ? (byte)1 : (byte)0;

        private static byte IsEphemeral(void* thisPtr, byte* obj) => GCHeapMemory.Contains(obj) ? (byte)1 : (byte)0;

        /// <summary>
        /// Everything this heap allocates stays in gen0 forever; anything else the EE asks
        /// about is in a frozen segment, which the C++ GC reports as the oldest generation.
        /// </summary>
        private static uint WhichGeneration(void* thisPtr, byte* obj) => GenerationOf(obj);

        internal static uint GenerationOf(byte* obj) => GCHeapMemory.Contains(obj) ? 0u : MaxGeneration;

        private static uint GetGenerationWithRange(void* thisPtr, byte* obj, byte** ppStart, byte** ppAllocated, byte** ppReserved)
        {
            *ppStart = GCHeapMemory.HeapStart;
            *ppAllocated = GCHeapMemory.HeapEnd;
            *ppReserved = GCHeapMemory.HeapEnd;
            return GenerationOf(obj);
        }

        // ------------------------------------------------------------------------------------
        // Frozen segments
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Port of <c>IGCHeap::RegisterFrozenSegment</c>. The EE calls this at startup for every
        /// module's frozen object region and fails fast if it gets back null, so it has to work
        /// even in a heap that does nothing else.
        /// </summary>
        private static segment_handle RegisterFrozenSegment(void* thisPtr, segment_info* pseginfo)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            segment_handle result = RegisterFrozenSegmentCore(pseginfo);
            criticalRegion.Exit();
            return result;
        }

        private static segment_handle RegisterFrozenSegmentCore(segment_info* pseginfo)
        {
            while (true)
            {
                int count = Volatile.Read(ref s_frozenSegmentCount);
                if (count == MaxFrozenSegments)
                {
                    return default;
                }

                if (Interlocked.CompareExchange(ref s_frozenSegmentCount, count + 1, count) == count)
                {
                    FrozenSegment* segment = s_frozenSegments + count;
                    segment->End = (nint)((byte*)pseginfo->pvMem + pseginfo->ibAllocated);

                    // Published last: FindFrozenSegment treats a zero Start as "not filled in
                    // yet" and skips the entry, so the range is never seen half-written.
                    Volatile.Write(ref segment->Start, (nint)((byte*)pseginfo->pvMem + pseginfo->ibFirstObject));
                    return new segment_handle(segment);
                }
            }
        }

        private static void UpdateFrozenSegment(void* thisPtr, segment_handle seg, byte* allocated, byte* committed)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            Volatile.Write(ref ((FrozenSegment*)seg.Value)->End, (nint)allocated);
            criticalRegion.Exit();
        }

        /// <summary>
        /// Retires a frozen segment. The table slot is not reused, because
        /// <see cref="s_frozenSegmentCount"/> only ever grows; the EE unregisters segments only
        /// at shutdown, so a free list would be dead code.
        /// </summary>
        private static void UnregisterFrozenSegment(void* thisPtr, segment_handle seg)
        {
            GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();
            FrozenSegment* segment = (FrozenSegment*)seg.Value;
            Volatile.Write(ref segment->Start, 0);
            segment->End = 0;
            criticalRegion.Exit();
        }

        private static byte IsInFrozenSegment(void* thisPtr, byte* obj) => FindFrozenSegment(obj) != null ? (byte)1 : (byte)0;

        private static FrozenSegment* FindFrozenSegment(byte* obj)
        {
            int count = Volatile.Read(ref s_frozenSegmentCount);
            for (int i = 0; i < count; i++)
            {
                FrozenSegment* segment = s_frozenSegments + i;
                nint start = Volatile.Read(ref segment->Start);
                nint end = Volatile.Read(ref segment->End);
                if (start != 0 && (nint)obj >= start && (nint)obj < end)
                {
                    return segment;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------------------------
        // Statistics and settings
        // ------------------------------------------------------------------------------------

        private static nuint GetTotalBytesInUse(void* thisPtr) => GCHeapMemory.BytesInUse;

        private static ulong GetTotalAllocatedBytes(void* thisPtr) => (ulong)Volatile.Read(ref s_totalAllocatedBytes);

        private static void GetMemoryInfo(
            void* thisPtr,
            ulong* highMemLoadThresholdBytes,
            ulong* totalAvailableMemoryBytes,
            ulong* lastRecordedMemLoadBytes,
            ulong* lastRecordedHeapSizeBytes,
            ulong* lastRecordedFragmentationBytes,
            ulong* totalCommittedBytes,
            ulong* promotedBytes,
            ulong* pinnedObjectCount,
            ulong* finalizationPendingCount,
            ulong* index,
            uint* generation,
            uint* pauseTimePct,
            byte* isCompaction,
            byte* isConcurrent,
            ulong* genInfoRaw,
            ulong* pauseInfoRaw,
            int kind)
        {
            *highMemLoadThresholdBytes = 0;
            *totalAvailableMemoryBytes = 0;
            *lastRecordedMemLoadBytes = 0;
            *lastRecordedHeapSizeBytes = GCHeapMemory.BytesInUse;
            *lastRecordedFragmentationBytes = 0;
            *totalCommittedBytes = GCHeapMemory.BytesInUse;
            *promotedBytes = 0;
            *pinnedObjectCount = 0;
            *finalizationPendingCount = 0;
            *index = 0;
            *generation = 0;
            *pauseTimePct = 0;
            *isCompaction = 0;
            *isConcurrent = 0;

            // Both are fixed-size arrays in the caller: RH_GH_MEMORY_INFO holds five
            // RH_GC_GENERATION_INFO (gen0-2, LOH, POH) of four ulongs each, and two pause
            // times. Matches the total_generation_count loop in GCHeap::GetMemoryInfo.
            for (int i = 0; i < 5 * 4; i++)
            {
                genInfoRaw[i] = 0;
            }

            pauseInfoRaw[0] = 0;
            pauseInfoRaw[1] = 0;
        }

        private static uint GetMemoryLoad(void* thisPtr) => 0;

        private static long GetTotalPauseDuration(void* thisPtr) => 0;

        private static int GetLastGCPercentTimeInGC(void* thisPtr) => 0;

        private static nuint GetLastGCGenerationSize(void* thisPtr, int gen) => 0;

        private static nuint GetLastGCStartTime(void* thisPtr, int generation) => 0;

        private static nuint GetLastGCDuration(void* thisPtr, int generation) => 0;

        private static nuint GetNow(void* thisPtr) => 0;

        private static ulong GetGenerationBudget(void* thisPtr, int generation) => 0;

        private static int GetGcLatencyMode(void* thisPtr) => 1;

        private static int SetGcLatencyMode(void* thisPtr, int newLatencyMode) => S_OK;

        private static int GetLOHCompactionMode(void* thisPtr) => 0;

        private static void SetLOHCompactionMode(void* thisPtr, int newLOHCompactionMode)
        {
        }

        private static int RefreshMemoryLimit(void* thisPtr) => S_OK;

        private static byte IsValidSegmentSize(void* thisPtr, nuint size) => 1;

        private static byte IsValidGen0MaxSize(void* thisPtr, nuint size) => 1;

        private static nuint GetValidSegmentSize(void* thisPtr, byte large_seg) =>
            (nuint)(GCHeapMemory.HeapEnd - GCHeapMemory.HeapStart);

        private static void SetReservedVMLimit(void* thisPtr, nuint vmlimit)
        {
        }

        /// <summary>
        /// Reports every configuration value to the EE, as <c>GCHeap::EnumerateConfigurationValues</c>
        /// of <c>interface.cpp</c> does. This is what <c>RhEnumerateConfigurationValues</c>, and
        /// through it <c>GC.GetConfigurationVariables</c>, calls.
        /// </summary>
        private static void EnumerateConfigurationValues(void* thisPtr, void* context, delegate* unmanaged<void*, byte*, byte*, GCConfigurationType, long, void> configurationValueFunc) =>
            GCConfig.EnumerateConfigurationValues(context, configurationValueFunc);

        // ------------------------------------------------------------------------------------
        // Full GC notifications and no-GC regions
        // ------------------------------------------------------------------------------------

        private static byte RegisterForFullGCNotification(void* thisPtr, uint gen2Percentage, uint lohPercentage) => 0;

        private static byte CancelFullGCNotification(void* thisPtr) => 0;

        private static int WaitForFullGCApproach(void* thisPtr, int millisecondsTimeout) => 0;

        private static int WaitForFullGCComplete(void* thisPtr, int millisecondsTimeout) => 0;

        /// <summary>
        /// The whole heap is a no-GC region, so entering one always succeeds and leaving one
        /// always finds it intact.
        /// </summary>
        private static int StartNoGCRegion(void* thisPtr, ulong totalSize, byte lohSizeKnown, ulong lohSize, byte disallowFullBlockingGC) =>
            (int)start_no_gc_region_status.start_no_gc_success;

        private static int EndNoGCRegion(void* thisPtr) => (int)end_no_gc_region_status.end_no_gc_success;

        private static enable_no_gc_region_callback_status EnableNoGCRegionCallback(void* thisPtr, NoGCRegionCallbackFinalizerWorkItem* callback, ulong callbackThreshold) =>
            enable_no_gc_region_callback_status.not_started;

        // ------------------------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------------------------

        private static void ControlEvents(void* thisPtr, GCEventKeyword keyword, GCEventLevel level)
        {
            GCEventStatus.Set(GCEventProvider.Default, keyword, level);
        }

        private static void ControlPrivateEvents(void* thisPtr, GCEventKeyword keyword, GCEventLevel level)
        {
            GCEventStatus.Set(GCEventProvider.Private, keyword, level);
        }

        private static void DiagScanFinalizeQueue(void* thisPtr, delegate* unmanaged<byte**, ScanContext*, uint, void> fn, ScanContext* sc)
        {
        }

        private static void DiagScanHandles(void* thisPtr, delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void> fn, int gen_number, ScanContext* context)
        {
        }

        private static void DiagScanDependentHandles(void* thisPtr, delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void> fn, int gen_number, ScanContext* context)
        {
        }

        private static void DiagDescrGenerations(void* thisPtr, delegate* unmanaged<void*, int, byte*, byte*, byte*, void> fn, void* context) =>
            fn(context, 0, GCHeapMemory.HeapStart, GCHeapMemory.HeapEnd, GCHeapMemory.HeapEnd);

        private static void DiagTraceGCSegments(void* thisPtr)
        {
        }

        private static void DiagGetGCSettings(void* thisPtr, EtwGCSettingsInfo* pGcSettings)
        {
            *pGcSettings = default;
            pGcSettings->loh_threshold = LargeObjectSize;
        }
    }
}
