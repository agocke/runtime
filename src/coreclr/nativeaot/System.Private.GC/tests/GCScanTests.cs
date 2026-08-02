// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the dependency-closed parts of gcscan.cpp. The production GCScan body is
// compiled directly into this assembly; only the IGCToCLR root-scan call beneath it is
// substituted.

using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCScanTests
{
    public GCScanTests()
    {
        GCScan.Initialize();
        GCToEEInterface.Reset();
    }

    [Fact]
    public void RuntimeStructuresStartInvalid()
    {
        Assert.False(GCScan.GetGcRuntimeStructuresValid());
    }

    [Fact]
    public void RuntimeStructureValidityCountsNestedInvalidRegions()
    {
        GCScan.GcRuntimeStructuresValid(1);
        Assert.True(GCScan.GetGcRuntimeStructuresValid());

        GCScan.GcRuntimeStructuresValid(0);
        GCScan.GcRuntimeStructuresValid(0);
        Assert.False(GCScan.GetGcRuntimeStructuresValid());

        GCScan.GcRuntimeStructuresValid(1);
        Assert.False(GCScan.GetGcRuntimeStructuresValid());

        GCScan.GcRuntimeStructuresValid(1);
        Assert.True(GCScan.GetGcRuntimeStructuresValid());
    }

    [Fact]
    public void GcScanRootsForwardsEveryArgument()
    {
        ScanContext sc = default;
        delegate*<byte**, ScanContext*, uint, void> callback = &Promote;

        GCScan.GcScanRoots(callback, 2, 3, &sc);

        Assert.Equal(1, GCToEEInterface.GcScanRootsCallCount);
        Assert.Equal((nuint)callback, GCToEEInterface.LastGcScanRootsCallback);
        Assert.Equal(2, GCToEEInterface.LastGcScanRootsCondemned);
        Assert.Equal(3, GCToEEInterface.LastGcScanRootsMaxGeneration);
        Assert.True(GCToEEInterface.LastGcScanRootsContext == &sc);
    }

    private static void Promote(byte** objectRef, ScanContext* sc, uint flags)
    {
    }
}
