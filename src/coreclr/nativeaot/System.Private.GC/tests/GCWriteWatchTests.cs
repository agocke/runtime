// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the write watch port of GCToOSInterface -- the translation of the
// SupportsWriteWatch probe, ResetWriteWatch and GetWriteWatch of gc/windows/gcenv.windows.cpp,
// and of the unsupported Unix versions in gc/unix/gcenv.unix.cpp.
//
// The ported bodies are the code under test. Only the Win32 declarations underneath them are
// substituted, by GCToOSInterface.Imports.Windows.TestHost.cs, which forwards each call to the
// real kernel and records its arguments. So the Windows tests check two things at once: that
// the arguments the port passes to the operating system are the ones the C++ passes, and that
// the write watch it sets up actually reports the pages the test wrote.
//
// On Unix there is nothing underneath to substitute: SupportsWriteWatch is a constant false and
// the other two only assert. The single test here pins that, because it is what makes the
// collector take the software write watch path on this platform.
//
// The expected flag values are written out here rather than read from the constants of the port,
// so that a wrong constant fails a test instead of being confirmed by it. The C++ values they
// stand for are named in the comments.

using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCWriteWatchTests
{
#if TARGET_WINDOWS
    // WRITE_WATCH_FLAG_RESET of <windows.h>.
    private const uint WRITE_WATCH_FLAG_RESET = 1;

    // MEM_RESERVE | MEM_WRITE_WATCH of <windows.h>.
    private const uint MEM_RESERVE_WRITE_WATCH = 0x00002000 | 0x00200000;

    private const uint MEM_RELEASE = 0x00008000;

    private static nuint PageSize => GCToOSInterface.GetPageSize();

    [Fact]
    public void WriteWatchIsSupportedOnWindows()
    {
        Assert.True(GCToOSInterface.SupportsWriteWatch());
    }

    /// <summary>
    /// Feature detection is a probe: one allocation-granularity write watch reservation, given
    /// straight back. Nothing may be left reserved afterwards, and the answer must come from
    /// the reservation rather than from a version test.
    /// </summary>
    [Fact]
    public void SupportsWriteWatchProbesWithAWriteWatchReservationAndReleasesIt()
    {
        GCToOSInterface.ResetRecording();
        bool supported = GCToOSInterface.SupportsWriteWatch();

        Assert.Equal(1, GCToOSInterface.VirtualAllocCount);
        Assert.False(GCToOSInterface.LastVirtualAlloc.numaAware);
        Assert.True(GCToOSInterface.LastVirtualAlloc.lpAddress == null);
        Assert.Equal(MEM_RESERVE_WRITE_WATCH, GCToOSInterface.LastVirtualAlloc.flAllocationType);

        // The probe size is g_SystemInfo.dwAllocationGranularity, which is 64 KB on every
        // Windows architecture the runtime supports.
        Assert.Equal((nuint)(64 * 1024), GCToOSInterface.LastVirtualAlloc.dwSize);

        Assert.True(supported);
        Assert.Equal(1, GCToOSInterface.VirtualFreeCount);
        Assert.Equal(MEM_RELEASE, GCToOSInterface.LastVirtualFree.dwFreeType);
        Assert.True(GCToOSInterface.LastVirtualFree.lpAddress == GCToOSInterface.LastVirtualAlloc.result);
    }

    /// <summary>
    /// The exercise the collector performs on a write watch region: reserve it with write
    /// watching on, commit it, write to some of its pages, and read back exactly those pages.
    /// </summary>
    [Fact]
    public void GetWriteWatchReportsTheWrittenPagesAndFillsInTheCount()
    {
        nuint pageSize = PageSize;
        const int PageCount = 8;
        nuint size = PageCount * pageSize;

        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.WriteWatch);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, size));

            void** addresses = stackalloc void*[PageCount];
            nuint count = PageCount;

            // Nothing has been written yet, so the region is clean.
            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.GetWriteWatch(false, region, size, addresses, &count));
            Assert.Equal((nuint)0, count);
            Assert.Equal(0u, GCToOSInterface.LastGetWriteWatch.dwFlags);
            Assert.True(GCToOSInterface.LastGetWriteWatch.lpBaseAddress == region);
            Assert.Equal(size, GCToOSInterface.LastGetWriteWatch.dwRegionSize);

            // The granularity the OS reports back is the page size, which is what the port
            // asserts.
            Assert.Equal((uint)pageSize, GCToOSInterface.LastGetWriteWatch.granularity);

            region[0] = 1;
            *(region + 3 * pageSize) = 1;

            count = PageCount;
            Assert.True(GCToOSInterface.GetWriteWatch(false, region, size, addresses, &count));
            Assert.Equal((nuint)2, count);

            // The order the addresses come back in is not part of the API contract, so the two
            // written pages are checked as a set.
            Assert.True(addresses[0] == region || addresses[1] == region);
            Assert.True(addresses[0] == region + 3 * pageSize || addresses[1] == region + 3 * pageSize);
            Assert.True(addresses[0] != addresses[1]);

            // A non-resetting read leaves the state alone, so the same two pages come back.
            count = PageCount;
            Assert.True(GCToOSInterface.GetWriteWatch(false, region, size, addresses, &count));
            Assert.Equal((nuint)2, count);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    /// <summary>
    /// The resetting read passes WRITE_WATCH_FLAG_RESET, and ResetWriteWatch clears the state
    /// without reading it. Both are what makes a second read report only what was written
    /// since.
    /// </summary>
    [Fact]
    public void ResettingClearsTheTrackedPages()
    {
        nuint pageSize = PageSize;
        const int PageCount = 4;
        nuint size = PageCount * pageSize;

        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.WriteWatch);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, size));

            void** addresses = stackalloc void*[PageCount];
            nuint count = PageCount;

            region[0] = 1;

            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.GetWriteWatch(true, region, size, addresses, &count));
            Assert.Equal(WRITE_WATCH_FLAG_RESET, GCToOSInterface.LastGetWriteWatch.dwFlags);
            Assert.Equal((nuint)1, count);
            Assert.True(addresses[0] == region);

            // The resetting read cleared the state, so nothing is reported now.
            count = PageCount;
            Assert.True(GCToOSInterface.GetWriteWatch(false, region, size, addresses, &count));
            Assert.Equal((nuint)0, count);

            // ResetWriteWatch clears it without reading it.
            *(region + pageSize) = 1;
            GCToOSInterface.ResetRecording();
            GCToOSInterface.ResetWriteWatch(region, size);
            Assert.Equal(1, GCToOSInterface.ResetWriteWatchCount);
            Assert.True(GCToOSInterface.LastResetWriteWatch.lpBaseAddress == region);
            Assert.Equal(size, GCToOSInterface.LastResetWriteWatch.dwRegionSize);
            Assert.Equal(0u, GCToOSInterface.LastResetWriteWatch.result);

            count = PageCount;
            Assert.True(GCToOSInterface.GetWriteWatch(false, region, size, addresses, &count));
            Assert.Equal((nuint)0, count);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    /// <summary>
    /// A buffer that is too small for the written pages is a failure, not a truncated result:
    /// GetWriteWatch returns ERROR_INSUFFICIENT_BUFFER, which the port reports as false, and
    /// the tracking state is left alone so that the caller can retry with a larger buffer.
    /// </summary>
    [Fact]
    public void GetWriteWatchFailsWhenTheBufferIsTooSmall()
    {
        nuint pageSize = PageSize;
        const int PageCount = 4;
        nuint size = PageCount * pageSize;

        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.WriteWatch);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, size));

            for (int i = 0; i < PageCount; i++)
            {
                *(region + (nuint)i * pageSize) = 1;
            }

            void** addresses = stackalloc void*[PageCount];
            nuint count = 1;

            GCToOSInterface.ResetRecording();
            Assert.False(GCToOSInterface.GetWriteWatch(true, region, size, addresses, &count));
            Assert.NotEqual(0u, GCToOSInterface.LastGetWriteWatch.result);

            // The failed resetting read did not reset anything.
            count = PageCount;
            Assert.True(GCToOSInterface.GetWriteWatch(false, region, size, addresses, &count));
            Assert.Equal((nuint)PageCount, count);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    /// <summary>
    /// A range that was never reserved with MEM_WRITE_WATCH is a failure rather than an empty
    /// result.
    /// </summary>
    [Fact]
    public void GetWriteWatchFailsOnARangeThatIsNotWatched()
    {
        nuint pageSize = PageSize;
        nuint size = 2 * pageSize;

        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, size));

            void** addresses = stackalloc void*[2];
            nuint count = 2;

            Assert.False(GCToOSInterface.GetWriteWatch(false, region, size, addresses, &count));
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }
#else
    /// <summary>
    /// Unix has no write watch. The collector asks once and uses software write watch instead,
    /// so this constant false is the whole of the platform behavior; ResetWriteWatch and
    /// GetWriteWatch are never reached and only assert, which is not something a test can
    /// exercise.
    /// </summary>
    [Fact]
    public void WriteWatchIsNotSupportedOnUnix()
    {
        Assert.False(GCToOSInterface.SupportsWriteWatch());
    }

    /// <summary>
    /// The Unix answer is a constant rather than the probe the Windows one performs: it must
    /// not reserve anything, because the Unix VirtualReserve asserts that nobody asks it for a
    /// write watch reservation.
    /// </summary>
    [Fact]
    public void SupportsWriteWatchTouchesNoMemoryOnUnix()
    {
        GCToOSInterface.ResetRecording();
        Assert.False(GCToOSInterface.SupportsWriteWatch());

        Assert.Equal(0, GCToOSInterface.MmapCount);
        Assert.Equal(0, GCToOSInterface.MunmapCount);
    }
#endif
}
