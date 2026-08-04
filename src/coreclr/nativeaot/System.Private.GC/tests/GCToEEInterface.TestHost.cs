// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Interface/GCToEEInterface.cs.
//
// GCConfig is the one ported file whose "underneath" is the EE rather than libc: every value it
// caches comes from GCToEEInterface, which in the shipping build is an indirect call through the
// IGCToCLR vtable the EE handed the GC at load time. There is no such instance in a test process,
// and a managed callback cannot stand in for one either -- the vtable slots are
// delegate* unmanaged[SuppressGCTransition], and CoreCLR rejects a call to an
// [UnmanagedCallersOnly] method through one -- so the substitution point is this class, exactly
// as the libc and Win32 imports are the substitution point for the platform ports.
//
// It models what nativeaot/Runtime/gcenv.ee.cpp does, because that is the behavior GCConfig is
// written against:
//
//   * the private key is looked up first, in the DOTNET_ environment settings;
//   * the public key is looked up only when the config has one, in the runtimeconfig knobs;
//   * a boolean is whatever the EE read compared against zero, so the GC only ever sees 0 or 1;
//   * an integer is the uint64 the EE read, reinterpreted as int64;
//   * a string is allocated by the EE and must be given back to FreeStringConfigValue.
//
// Every call is recorded, so the tests can assert the key sequence GCConfig asks for rather than
// only the values it ends up with.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Internal.Runtime.GarbageCollection;

/// <summary>One configuration request, as <c>GCConfig</c> made it.</summary>
internal sealed class ConfigRequest
{
    public ConfigRequest(string kind, string privateKey, string publicKey)
    {
        Kind = kind;
        PrivateKey = privateKey;
        PublicKey = publicKey;
    }

    /// <summary>"bool", "int" or "string": which of the three EE getters was called.</summary>
    public string Kind { get; }

    /// <summary>The private key the GC passed. Never null: every config has one.</summary>
    public string PrivateKey { get; }

    /// <summary>The public key the GC passed, or null where it passed a null pointer.</summary>
    public string PublicKey { get; }

    public override string ToString() => $"{Kind}({PrivateKey}, {PublicKey ?? "null"})";
}

internal static unsafe class GCToEEInterface
{
    internal static class FiredEvent
    {
        public const string None = nameof(None);
        public const string GCStart_V2 = nameof(GCStart_V2);
        public const string GCEnd_V1 = nameof(GCEnd_V1);
        public const string GCGenerationRange = nameof(GCGenerationRange);
        public const string GCHeapStats_V2 = nameof(GCHeapStats_V2);
        public const string GCCreateSegment_V1 = nameof(GCCreateSegment_V1);
        public const string GCFreeSegment_V1 = nameof(GCFreeSegment_V1);
        public const string GCCreateConcurrentThread_V1 = nameof(GCCreateConcurrentThread_V1);
        public const string GCTerminateConcurrentThread_V1 = nameof(GCTerminateConcurrentThread_V1);
        public const string GCTriggered = nameof(GCTriggered);
        public const string GCMarkWithType = nameof(GCMarkWithType);
        public const string GCJoin_V2 = nameof(GCJoin_V2);
        public const string GCGlobalHeapHistory_V4 = nameof(GCGlobalHeapHistory_V4);
        public const string GCAllocationTick_V1 = nameof(GCAllocationTick_V1);
        public const string GCAllocationTick_V4 = nameof(GCAllocationTick_V4);
        public const string PinObjectAtGCTime = nameof(PinObjectAtGCTime);
        public const string PinPlugAtGCTime = nameof(PinPlugAtGCTime);
        public const string GCPerHeapHistory_V3 = nameof(GCPerHeapHistory_V3);
        public const string GCLOHCompact = nameof(GCLOHCompact);
        public const string GCFitBucketInfo = nameof(GCFitBucketInfo);
        public const string BGCBegin = nameof(BGCBegin);
        public const string BGC1stNonConEnd = nameof(BGC1stNonConEnd);
        public const string BGC1stConEnd = nameof(BGC1stConEnd);
        public const string BGC1stSweepEnd = nameof(BGC1stSweepEnd);
        public const string BGC2ndNonConBegin = nameof(BGC2ndNonConBegin);
        public const string BGC2ndNonConEnd = nameof(BGC2ndNonConEnd);
        public const string BGC2ndConBegin = nameof(BGC2ndConBegin);
        public const string BGC2ndConEnd = nameof(BGC2ndConEnd);
        public const string BGCDrainMark = nameof(BGCDrainMark);
        public const string BGCRevisit = nameof(BGCRevisit);
        public const string BGCOverflow_V1 = nameof(BGCOverflow_V1);
        public const string BGCAllocWaitBegin = nameof(BGCAllocWaitBegin);
        public const string BGCAllocWaitEnd = nameof(BGCAllocWaitEnd);
        public const string GCFullNotify_V1 = nameof(GCFullNotify_V1);
        public const string SetGCHandle = nameof(SetGCHandle);
        public const string PrvSetGCHandle = nameof(PrvSetGCHandle);
        public const string DestroyGCHandle = nameof(DestroyGCHandle);
        public const string PrvDestroyGCHandle = nameof(PrvDestroyGCHandle);
        public const string Dynamic = nameof(Dynamic);
    }

    internal static void* LastInitializedGCToCLR { get; private set; }

    internal static int InitializeCallCount { get; private set; }

    /// <summary>Values reachable through the private key, i.e. the DOTNET_ settings.</summary>
    private static readonly Dictionary<string, ulong> s_privateValues = new(StringComparer.Ordinal);

    /// <summary>Values reachable through the public key, i.e. the runtimeconfig knobs.</summary>
    private static readonly Dictionary<string, ulong> s_publicValues = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> s_privateStrings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> s_publicStrings = new(StringComparer.Ordinal);

    /// <summary>Every request, in the order the GC made it.</summary>
    internal static List<ConfigRequest> Requests { get; } = new();

    /// <summary>Strings handed to the GC that have not been given back yet.</summary>
    internal static List<IntPtr> OutstandingStrings { get; } = new();

    /// <summary>Every pointer passed to <see cref="FreeStringConfigValue"/>, including null.</summary>
    internal static List<IntPtr> FreedStrings { get; } = new();

    /// <summary>How many times the port called <see cref="StompWriteBarrier"/>.</summary>
    internal static int StompWriteBarrierCallCount { get; private set; }

    /// <summary>
    /// The <see cref="WriteBarrierParameters"/> the most recent <see cref="StompWriteBarrier"/>
    /// call passed, copied out of the pointer -- which the caller may free or overwrite on
    /// return -- so tests can inspect it afterwards.
    /// </summary>
    internal static WriteBarrierParameters LastStompWriteBarrier { get; private set; }

    internal static int GcScanRootsCallCount { get; private set; }

    internal static nuint LastGcScanRootsCallback { get; private set; }

    internal static int LastGcScanRootsCondemned { get; private set; }

    internal static int LastGcScanRootsMaxGeneration { get; private set; }

    internal static ScanContext* LastGcScanRootsContext { get; private set; }

    internal static int GetThreadCallCount { get; private set; }

    internal static void* CurrentThread { get; set; }

    internal static string LastFiredEvent { get; private set; }

    internal static string LastDynamicEventName { get; private set; }

    internal static byte[] LastDynamicEventPayload { get; private set; }

    internal static int GCCreateSegmentCallCount { get; private set; }

    internal static void* LastGCCreateSegmentAddress { get; private set; }

    internal static nuint LastGCCreateSegmentSize { get; private set; }

    internal static uint LastGCCreateSegmentType { get; private set; }

    internal static void Reset()
    {
        LastInitializedGCToCLR = null;
        InitializeCallCount = 0;
        s_privateValues.Clear();
        s_publicValues.Clear();
        s_privateStrings.Clear();
        s_publicStrings.Clear();
        Requests.Clear();
        FreedStrings.Clear();
        WriteWithoutProviding = null;
        StompWriteBarrierCallCount = 0;
        LastStompWriteBarrier = default;
        GcScanRootsCallCount = 0;
        LastGcScanRootsCallback = 0;
        LastGcScanRootsCondemned = 0;
        LastGcScanRootsMaxGeneration = 0;
        LastGcScanRootsContext = null;
        GetThreadCallCount = 0;
        CurrentThread = null;
        LastFiredEvent = FiredEvent.None;
        LastDynamicEventName = null;
        LastDynamicEventPayload = null;
        GCCreateSegmentCallCount = 0;
        LastGCCreateSegmentAddress = null;
        LastGCCreateSegmentSize = 0;
        LastGCCreateSegmentType = 0;

        foreach (IntPtr outstanding in OutstandingStrings)
        {
            NativeMemory.Free((void*)outstanding);
        }

        OutstandingStrings.Clear();
    }

    public static void Initialize(void* theGCToCLR)
    {
        LastInitializedGCToCLR = theGCToCLR;
        InitializeCallCount++;
    }

    public static uint GetCurrentProcessCpuCount() => (uint)Environment.ProcessorCount;

    /// <summary>
    /// Substitute for the indirect <c>IGCToCLR::StompWriteBarrier</c> call.
    /// <see cref="SoftwareWriteWatch.EnableForGCHeap"/> and
    /// <see cref="SoftwareWriteWatch.DisableForGCHeap"/> are the only callers this file's tests
    /// exercise, so recording the arguments is enough; no real write barrier is bashed.
    /// </summary>
    public static void StompWriteBarrier(WriteBarrierParameters* args)
    {
        StompWriteBarrierCallCount++;
        LastStompWriteBarrier = *args;
    }

    public static void GcScanRoots(
        delegate*<byte**, ScanContext*, uint, void> fn,
        int condemned,
        int max_gen,
        ScanContext* sc)
    {
        GcScanRootsCallCount++;
        LastGcScanRootsCallback = (nuint)fn;
        LastGcScanRootsCondemned = condemned;
        LastGcScanRootsMaxGeneration = max_gen;
        LastGcScanRootsContext = sc;
    }

    public static void* GetThread()
    {
        GetThreadCallCount++;
        return CurrentThread;
    }

    public static void FireDynamicEvent(byte* name, void* payload, uint payloadSize)
    {
        LastFiredEvent = FiredEvent.Dynamic;
        LastDynamicEventName = Marshal.PtrToStringUTF8((nint)name);
        LastDynamicEventPayload = new ReadOnlySpan<byte>(payload, checked((int)payloadSize)).ToArray();
    }

    public static void FireGCStart_V2(uint count, uint depth, uint reason, uint type) => LastFiredEvent = FiredEvent.GCStart_V2;
    public static void FireGCEnd_V1(uint count, uint depth) => LastFiredEvent = FiredEvent.GCEnd_V1;
    public static void FireGCGenerationRange(byte generation, void* rangeStart, ulong rangeUsedLength, ulong rangeReservedLength) => LastFiredEvent = FiredEvent.GCGenerationRange;
    public static void FireGCHeapStats_V2(ulong generationSize0, ulong totalPromotedSize0, ulong generationSize1, ulong totalPromotedSize1, ulong generationSize2, ulong totalPromotedSize2, ulong generationSize3, ulong totalPromotedSize3, ulong generationSize4, ulong totalPromotedSize4, ulong finalizationPromotedSize, ulong finalizationPromotedCount, uint pinnedObjectCount, uint sinkBlockCount, uint gcHandleCount) => LastFiredEvent = FiredEvent.GCHeapStats_V2;
    public static void FireGCCreateSegment_V1(void* address, nuint size, uint type)
    {
        LastFiredEvent = FiredEvent.GCCreateSegment_V1;
        GCCreateSegmentCallCount++;
        LastGCCreateSegmentAddress = address;
        LastGCCreateSegmentSize = size;
        LastGCCreateSegmentType = type;
    }
    public static void FireGCFreeSegment_V1(void* address) => LastFiredEvent = FiredEvent.GCFreeSegment_V1;
    public static void FireGCCreateConcurrentThread_V1() => LastFiredEvent = FiredEvent.GCCreateConcurrentThread_V1;
    public static void FireGCTerminateConcurrentThread_V1() => LastFiredEvent = FiredEvent.GCTerminateConcurrentThread_V1;
    public static void FireGCTriggered(uint reason) => LastFiredEvent = FiredEvent.GCTriggered;
    public static void FireGCMarkWithType(uint heapNum, uint type, ulong bytes) => LastFiredEvent = FiredEvent.GCMarkWithType;
    public static void FireGCJoin_V2(uint heap, uint joinTime, uint joinType, uint joinId) => LastFiredEvent = FiredEvent.GCJoin_V2;
    public static void FireGCGlobalHeapHistory_V4(ulong finalYoungestDesired, int numHeaps, uint condemnedGeneration, uint gen0ReductionCount, uint reason, uint globalMechanisms, uint pauseMode, uint memoryPressure, uint condemnReasons0, uint condemnReasons1, uint count, uint valuesLen, void* values) => LastFiredEvent = FiredEvent.GCGlobalHeapHistory_V4;
    public static void FireGCAllocationTick_V1(uint allocationAmount, uint allocationKind) => LastFiredEvent = FiredEvent.GCAllocationTick_V1;
    public static void FireGCAllocationTick_V4(ulong allocationAmount, uint allocationKind, uint heapIndex, void* objectAddress, ulong objectSize) => LastFiredEvent = FiredEvent.GCAllocationTick_V4;
    public static void FirePinObjectAtGCTime(void* objectAddress, byte** objectHandle) => LastFiredEvent = FiredEvent.PinObjectAtGCTime;
    public static void FirePinPlugAtGCTime(byte* plugStart, byte* plugEnd, byte* gapBeforeSize) => LastFiredEvent = FiredEvent.PinPlugAtGCTime;
    public static void FireGCPerHeapHistory_V3(void* freeListAllocated, void* freeListRejected, void* endOfSegAllocated, void* condemnedAllocated, void* pinnedAllocated, void* pinnedAllocatedAdvance, uint runningFreeListEfficiency, uint condemnReasons0, uint condemnReasons1, uint compactMechanisms, uint expandMechanisms, uint heapIndex, void* extraGen0Commit, uint count, uint valuesLen, void* values) => LastFiredEvent = FiredEvent.GCPerHeapHistory_V3;
    public static void FireGCLOHCompact(ushort count, uint valuesLen, void* values) => LastFiredEvent = FiredEvent.GCLOHCompact;
    public static void FireGCFitBucketInfo(ushort kind, nuint totalSize, ushort count, uint valuesLen, void* values) => LastFiredEvent = FiredEvent.GCFitBucketInfo;
    public static void FireBGCBegin() => LastFiredEvent = FiredEvent.BGCBegin;
    public static void FireBGC1stNonConEnd() => LastFiredEvent = FiredEvent.BGC1stNonConEnd;
    public static void FireBGC1stConEnd() => LastFiredEvent = FiredEvent.BGC1stConEnd;
    public static void FireBGC1stSweepEnd(uint genNumber) => LastFiredEvent = FiredEvent.BGC1stSweepEnd;
    public static void FireBGC2ndNonConBegin() => LastFiredEvent = FiredEvent.BGC2ndNonConBegin;
    public static void FireBGC2ndNonConEnd() => LastFiredEvent = FiredEvent.BGC2ndNonConEnd;
    public static void FireBGC2ndConBegin() => LastFiredEvent = FiredEvent.BGC2ndConBegin;
    public static void FireBGC2ndConEnd() => LastFiredEvent = FiredEvent.BGC2ndConEnd;
    public static void FireBGCDrainMark(ulong objects) => LastFiredEvent = FiredEvent.BGCDrainMark;
    public static void FireBGCRevisit(ulong pages, ulong objects, uint isLarge) => LastFiredEvent = FiredEvent.BGCRevisit;
    public static void FireBGCOverflow_V1(ulong min, ulong max, ulong objects, uint isLarge, uint genNumber) => LastFiredEvent = FiredEvent.BGCOverflow_V1;
    public static void FireBGCAllocWaitBegin(uint reason) => LastFiredEvent = FiredEvent.BGCAllocWaitBegin;
    public static void FireBGCAllocWaitEnd(uint reason) => LastFiredEvent = FiredEvent.BGCAllocWaitEnd;
    public static void FireGCFullNotify_V1(uint genNumber, uint isAlloc) => LastFiredEvent = FiredEvent.GCFullNotify_V1;
    public static void FireSetGCHandle(void* handleId, void* objectId, uint kind, uint generation) => LastFiredEvent = FiredEvent.SetGCHandle;
    public static void FirePrvSetGCHandle(void* handleId, void* objectId, uint kind, uint generation) => LastFiredEvent = FiredEvent.PrvSetGCHandle;
    public static void FireDestroyGCHandle(void* handleId) => LastFiredEvent = FiredEvent.DestroyGCHandle;
    public static void FirePrvDestroyGCHandle(void* handleId) => LastFiredEvent = FiredEvent.PrvDestroyGCHandle;

    public static void WalkAsyncPinned(byte* @object, void* context, delegate* unmanaged<byte*, byte*, void*, void> callback)
    {
    }

    internal static void SetPrivateValue(string privateKey, ulong value) => s_privateValues[privateKey] = value;

    internal static void SetPublicValue(string publicKey, ulong value) => s_publicValues[publicKey] = value;

    internal static void SetPrivateString(string privateKey, string value) => s_privateStrings[privateKey] = value;

    internal static void SetPublicString(string publicKey, string value) => s_publicStrings[publicKey] = value;

    /// <summary>
    /// When non-null, the boolean and integer getters write this value through the out pointer
    /// and then report that the config was not provided, which is what a misbehaving EE does and
    /// what the C++ leaves visible by passing the address of the cached value straight down.
    /// </summary>
    internal static ulong? WriteWithoutProviding { get; set; }

    public static byte GetBooleanConfigValue(byte* privateKey, byte* publicKey, byte* value)
    {
        string privateName = ToManagedString(privateKey);
        string publicName = ToManagedString(publicKey);
        Requests.Add(new ConfigRequest("bool", privateName, publicName));

        if (WriteWithoutProviding is ulong written)
        {
            *value = (byte)(written != 0 ? 1 : 0);
            return 0;
        }

        if (s_privateValues.TryGetValue(privateName, out ulong privateValue))
        {
            *value = (byte)(privateValue != 0 ? 1 : 0);
            return 1;
        }

        if (publicName is not null && s_publicValues.TryGetValue(publicName, out ulong publicValue))
        {
            *value = (byte)(publicValue != 0 ? 1 : 0);
            return 1;
        }

        return 0;
    }

    public static byte GetIntConfigValue(byte* privateKey, byte* publicKey, long* value)
    {
        string privateName = ToManagedString(privateKey);
        string publicName = ToManagedString(publicKey);
        Requests.Add(new ConfigRequest("int", privateName, publicName));

        if (WriteWithoutProviding is ulong written)
        {
            *value = (long)written;
            return 0;
        }

        if (s_privateValues.TryGetValue(privateName, out ulong privateValue))
        {
            *value = (long)privateValue;
            return 1;
        }

        if (publicName is not null && s_publicValues.TryGetValue(publicName, out ulong publicValue))
        {
            *value = (long)publicValue;
            return 1;
        }

        return 0;
    }

    public static byte GetStringConfigValue(byte* privateKey, byte* publicKey, byte** value)
    {
        string privateName = ToManagedString(privateKey);
        string publicName = ToManagedString(publicKey);
        Requests.Add(new ConfigRequest("string", privateName, publicName));

        if (s_privateStrings.TryGetValue(privateName, out string privateValue))
        {
            *value = Allocate(privateValue);
            return 1;
        }

        if (publicName is not null && s_publicStrings.TryGetValue(publicName, out string publicValue))
        {
            *value = Allocate(publicValue);
            return 1;
        }

        return 0;
    }

    public static void FreeStringConfigValue(byte* value)
    {
        FreedStrings.Add((IntPtr)value);

        if (value is null)
        {
            return;
        }

        // A double free, or a free of something the EE never handed out, is a test failure that
        // has to be visible rather than a heap corruption.
        if (!OutstandingStrings.Remove((IntPtr)value))
        {
            throw new InvalidOperationException("Freed a config string that was not outstanding.");
        }

        NativeMemory.Free(value);
    }

    /// <summary>The UTF-8 bytes of a string the tests handed out, as the caller sees them.</summary>
    internal static string ReadString(byte* value) => ToManagedString(value);

    private static byte* Allocate(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Span<byte> destination = new Span<byte>(buffer, length + 1);
        Encoding.UTF8.GetBytes(value, destination);
        destination[length] = 0;

        OutstandingStrings.Add((IntPtr)buffer);
        return buffer;
    }

    private static string ToManagedString(byte* value)
    {
        if (value is null)
        {
            return null;
        }

        int length = 0;
        while (value[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(value, length);
    }
}
