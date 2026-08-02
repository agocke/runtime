// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the virtual memory port of GCToOSInterface -- the translation of the
// mmap/mprotect/madvise sequences of gc/unix/gcenv.unix.cpp and of the VirtualAlloc/VirtualFree
// flag combinations of gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only the libc/Win32 declarations underneath them
// are substituted, by GCToOSInterface.Imports.*.TestHost.cs, which forwards each call to the
// real kernel and records its arguments. So these tests check two things at once: that the
// arguments the port passes to the operating system are the ones the C++ passes, and that the
// resulting memory behaves the way the collector requires.
//
// The expected flag values are written out here rather than read from the constants of the port,
// so that a wrong constant fails a test instead of being confirmed by it. The C++ values they
// stand for are named in the comments.

using System;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCVirtualMemoryTests
{
    private static nuint PageSize => GCToOSInterface.GetPageSize();

    /// <summary>
    /// An address that no mapping can ever occupy: Linux refuses to map below
    /// <c>vm.mmap_min_addr</c> and Windows reserves the first 64 KB as the null pointer region.
    /// The negative tests use it rather than an address they have just released, which another
    /// thread of the test process could have taken over in the meantime.
    /// </summary>
    private static void* NeverMappedAddress => (void*)(nint)0x1000;

    [Fact]
    public void GetPageSizeIsTheOperatingSystemPageSize()
    {
        Assert.Equal((nuint)Environment.SystemPageSize, GCToOSInterface.GetPageSize());
    }

    [Fact]
    public void GetVirtualMemoryLimitAndMaxAddressAreUsable()
    {
        nuint maxAddress = GCToOSInterface.GetVirtualMemoryMaxAddress();
        nuint limit = GCToOSInterface.GetVirtualMemoryLimit();

        Assert.NotEqual((nuint)0, maxAddress);
        Assert.NotEqual((nuint)0, limit);

#if TARGET_WINDOWS
        // GetVirtualMemoryMaxAddress is GetVirtualMemoryLimit on Windows. The two calls sample
        // the available address space at different moments, so they are only equal in order of
        // magnitude; what is being checked is that one is the other.
        Assert.True(maxAddress >= limit / 2 && maxAddress <= limit * 2);
#else
        // On Unix the maximum address is a constant -- 128TB on 64-bit, except RISC-V -- and
        // the limit is either RLIMIT_AS or that same constant.
        if (sizeof(nint) == 8)
        {
            Assert.True(maxAddress == unchecked((nuint)(1UL << 47)) || maxAddress == unchecked((nuint)(1UL << 38)));
        }
        else
        {
            Assert.Equal(unchecked((nuint)(-1)), maxAddress);
        }

        Assert.True(limit <= maxAddress);
#endif
    }

    /// <summary>
    /// The exercise the collector actually performs on a region: reserve address space, commit
    /// part of it, use it, reset it, decommit it, commit it again and release the whole
    /// reservation. It runs on raw pages throughout and never touches the managed heap.
    /// </summary>
    [Fact]
    public void ReservedPagesCanBeCommittedWrittenResetDecommittedAndReleased()
    {
        nuint pageSize = PageSize;
        nuint size = 4 * pageSize;
        const nuint Alignment = 64 * 1024;

        byte* region = GCToOSInterface.VirtualReserve(size, Alignment, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);
        Assert.Equal((nuint)0, (nuint)region & (Alignment - 1));

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, 2 * pageSize));

            // Freshly committed pages must read as zero: the collector hands them out without
            // clearing them.
            AssertRangeIsZero(region, 2 * pageSize);

            Fill(region, 2 * pageSize, 0xCD);
            AssertRangeIs(region, 2 * pageSize, 0xCD);

            // A reset says the contents are no longer of interest but leaves the range
            // committed and accessible. What it does not promise is that the contents survive:
            // MEM_RESET and MADV_FREE both allow the pages to be dropped at any moment, so the
            // range is committed again -- which the GC does too -- before it is used.
            Assert.True(GCToOSInterface.VirtualReset(region, 2 * pageSize, false));
            Assert.True(GCToOSInterface.VirtualCommit(region, 2 * pageSize));
            Fill(region, 2 * pageSize, 0xAB);
            AssertRangeIs(region, 2 * pageSize, 0xAB);

            // A decommit followed by a commit must produce zeroed pages again.
            Assert.True(GCToOSInterface.VirtualDecommit(region, pageSize));
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));
            AssertRangeIsZero(region, pageSize);

            // The rest of the reservation is untouched by the decommit.
            AssertRangeIs(region + pageSize, pageSize, 0xAB);

            // Committing the remaining reserved pages still works.
            Assert.True(GCToOSInterface.VirtualCommit(region + 2 * pageSize, 2 * pageSize));
            AssertRangeIsZero(region + 2 * pageSize, 2 * pageSize);
            Fill(region + 2 * pageSize, 2 * pageSize, 0x5A);
            AssertRangeIs(region + 2 * pageSize, 2 * pageSize, 0x5A);

            Assert.True(GCToOSInterface.VirtualRelease(region, size));
            region = null;
        }
        finally
        {
            if (region != null)
            {
                GCToOSInterface.VirtualRelease(region, size);
            }
        }
    }

    [Fact]
    public void ReserveReturnsNullWhenTheAddressSpaceCannotSatisfyTheRequest()
    {
        // Half the address space, which no process can reserve.
        nuint size = unchecked((nuint)(-1)) / 2;
        Assert.True(GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None) == null);
    }

    [Fact]
    public void CommitAndResetFailOnARangeThatIsNotReserved()
    {
        // Nothing is mapped there, so the operations on it have to report failure rather than
        // succeed silently.
        Assert.False(GCToOSInterface.VirtualCommit(NeverMappedAddress, PageSize));
        Assert.False(GCToOSInterface.VirtualReset(NeverMappedAddress, PageSize, false));
    }

    private static void Fill(byte* address, nuint size, byte value)
    {
        for (nuint i = 0; i < size; i++)
        {
            address[i] = value;
        }
    }

    private static void AssertRangeIs(byte* address, nuint size, byte value)
    {
        for (nuint i = 0; i < size; i++)
        {
            if (address[i] != value)
            {
                Assert.Fail($"byte {i} of the range is {address[i]}, expected {value}");
            }
        }
    }

    private static void AssertRangeIsZero(byte* address, nuint size) => AssertRangeIs(address, size, 0);

#if !TARGET_WINDOWS
    //
    // The Unix flag translation. The expected values are the <sys/mman.h> ones of the platform
    // this assembly was compiled for.
    //

    private const int PROT_NONE = 0x0;
    private const int PROT_READ_WRITE = 0x1 | 0x2;

#if TARGET_APPLE
    private const int MAP_ANON = 0x1000;
    private const int MAP_PRIVATE = 0x0002;
    private const int MAP_FIXED = 0x0010;
    private const int LargePagesFlag = 0x10000; // VM_FLAGS_SUPERPAGE_SIZE_ANY
    private const int MADV_FREE = 5;
    private static bool HasCoredumpAdvice => false; // MADV_DONTDUMP / MADV_DODUMP are Linux-only
#elif TARGET_FREEBSD
    private const int MAP_ANON = 0x1000;
    private const int MAP_PRIVATE = 0x0002;
    private const int MAP_FIXED = 0x0010;
    private const int LargePagesFlag = 0;
    private const int MADV_FREE = 5;
    private static bool HasCoredumpAdvice => false;
#elif TARGET_OPENBSD
    private const int MAP_ANON = 0x1000;
    private const int MAP_PRIVATE = 0x0002;
    private const int MAP_FIXED = 0x0010;
    private const int LargePagesFlag = 0;
    private const int MADV_FREE = 6;
    private static bool HasCoredumpAdvice => false;
#else
    private const int MAP_ANON = 0x20;
    private const int MAP_PRIVATE = 0x02;
    private const int MAP_FIXED = 0x10;
    private const int LargePagesFlag = 0x40000; // MAP_HUGETLB
    private const int MADV_DONTDUMP = 16;
    private const int MADV_DODUMP = 17;
    private const int MADV_FREE = 8;
    private static bool HasCoredumpAdvice => true;
#endif

    [Fact]
    public void ReserveMapsAnonymousPrivateMemoryWithNoAccess()
    {
        nuint pageSize = PageSize;
        nuint size = 2 * pageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.Equal(1, GCToOSInterface.MmapCount);
            Assert.True(GCToOSInterface.LastMmap.addr == null);
            Assert.Equal(PROT_NONE, GCToOSInterface.LastMmap.prot);
            Assert.Equal(MAP_ANON | MAP_PRIVATE, GCToOSInterface.LastMmap.flags);
            Assert.Equal(-1, GCToOSInterface.LastMmap.fd);
            Assert.Equal((nint)0, GCToOSInterface.LastMmap.offset);

            // An alignment below the page size is raised to it, which makes the over-allocation
            // zero and leaves nothing to trim.
            Assert.Equal(size, GCToOSInterface.LastMmap.length);
            Assert.Equal(0, GCToOSInterface.MunmapCount);

            if (HasCoredumpAdvice)
            {
                // A reservation is not committed, so it is kept out of coredumps.
                Assert.Equal(1, GCToOSInterface.MadviseCount);
                Assert.True(GCToOSInterface.LastMadvise.addr == region);
                Assert.Equal(size, GCToOSInterface.LastMadvise.length);
                Assert.Equal(16, GCToOSInterface.LastMadvise.arg); // MADV_DONTDUMP
            }
            else
            {
                Assert.Equal(0, GCToOSInterface.MadviseCount);
            }
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(16u)]
    public void ReserveOverAllocatesForAlignmentAndTrimsThePadding(uint alignmentInPages)
    {
        nuint pageSize = PageSize;
        nuint alignment = alignmentInPages * pageSize;
        nuint size = 3 * pageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, alignment, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            nuint alignedSize = size + (alignment - pageSize);
            byte* rawMapping = (byte*)GCToOSInterface.LastMmap.result;

            Assert.Equal(alignedSize, GCToOSInterface.LastMmap.length);
            Assert.Equal((nuint)0, (nuint)region & (alignment - 1));
            Assert.True(region >= rawMapping);
            Assert.True(region + size <= rawMapping + alignedSize);

            // Exactly the over-allocated bytes are given back, one call per non-empty side, and
            // each call covers exactly that side. The ranges are checked against what was
            // unmapped rather than by probing the address space for them, which would race with
            // the other threads of the test process.
            nuint startPadding = (nuint)(region - rawMapping);
            nuint endPadding = alignedSize - (startPadding + size);
            int expectedCalls = (startPadding != 0 ? 1 : 0) + (endPadding != 0 ? 1 : 0);

            Assert.Equal(alignedSize - size, GCToOSInterface.MunmapTotalLength);
            Assert.Equal(expectedCalls, GCToOSInterface.MunmapCount);

            int call = 0;
            if (startPadding != 0)
            {
                Assert.True(GCToOSInterface.MunmapCalls[call].addr == rawMapping);
                Assert.Equal(startPadding, GCToOSInterface.MunmapCalls[call].length);
                Assert.Equal(0, GCToOSInterface.MunmapCalls[call].result);
                call++;
            }

            if (endPadding != 0)
            {
                Assert.True(GCToOSInterface.MunmapCalls[call].addr == region + size);
                Assert.Equal(endPadding, GCToOSInterface.MunmapCalls[call].length);
                Assert.Equal(0, GCToOSInterface.MunmapCalls[call].result);
            }

            // The kept range is intact.
            Assert.True(GCToOSInterface.VirtualCommit(region, size));
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void CommitMakesTheRangeReadWriteAndDumpable()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));

            Assert.Equal(1, GCToOSInterface.MprotectCount);
            Assert.True(GCToOSInterface.LastMprotect.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastMprotect.length);
            Assert.Equal(PROT_READ_WRITE, GCToOSInterface.LastMprotect.arg);

            if (HasCoredumpAdvice)
            {
                // Already reserved memory was advised out of the coredump; committing it puts
                // it back in.
                Assert.Equal(1, GCToOSInterface.MadviseCount);
                Assert.Equal(17, GCToOSInterface.LastMadvise.arg); // MADV_DODUMP
            }

            // No node was asked for, so the NUMA binding is not attempted.
            Assert.Equal(0, GCToOSInterface.BindMemoryPolicyCount);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

    [Fact]
    public void CommitBindsTheRangeWhenANodeIsRequested()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize, 1));

            Assert.Equal(1, GCToOSInterface.BindMemoryPolicyCount);
            Assert.True(GCToOSInterface.LastBindMemoryPolicy.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastBindMemoryPolicy.length);
            Assert.Equal(1, GCToOSInterface.LastBindMemoryPolicy.arg);

            // A failed commit must not try to place the range at all.
            GCToOSInterface.ResetRecording();
            Assert.False(GCToOSInterface.VirtualCommit(NeverMappedAddress, pageSize, 1));
            Assert.Equal(0, GCToOSInterface.BindMemoryPolicyCount);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

    [Fact]
    public void DecommitReplacesTheRangeWithAFreshInaccessibleMapping()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));

            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualDecommit(region, pageSize));

            // mmap, not mprotect: the kernel is told the pages are no longer needed, and the
            // GC depends on re-committed pages reading as zero.
            Assert.Equal(1, GCToOSInterface.MmapCount);
            Assert.Equal(0, GCToOSInterface.MprotectCount);
            Assert.True(GCToOSInterface.LastMmap.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastMmap.length);
            Assert.Equal(PROT_NONE, GCToOSInterface.LastMmap.prot);
            Assert.Equal(MAP_FIXED | MAP_ANON | MAP_PRIVATE, GCToOSInterface.LastMmap.flags);

            if (HasCoredumpAdvice)
            {
                Assert.Equal(1, GCToOSInterface.MadviseCount);
                Assert.Equal(16, GCToOSInterface.LastMadvise.arg); // MADV_DONTDUMP
            }
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

    [Fact]
    public void ResetAdvisesThatTheRangeIsNoLongerNeeded()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));

            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualReset(region, pageSize, false));

            // The range stays committed: no remap and no protection change.
            Assert.Equal(0, GCToOSInterface.MmapCount);
            Assert.Equal(0, GCToOSInterface.MprotectCount);
            Assert.Equal(0, GCToOSInterface.MunmapCount);

            Assert.Equal(HasCoredumpAdvice ? 2 : 1, GCToOSInterface.MadviseCount);
            Assert.True(GCToOSInterface.LastMadvise.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastMadvise.length);
            Assert.Equal(MADV_FREE, GCToOSInterface.LastMadvise.arg);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

    [Fact]
    public void ReleaseUnmapsTheWholeRangeAndRejectsAnEmptyOne()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(2 * pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        GCToOSInterface.ResetRecording();

        // munmap rejects a zero length, so the failure must be reported rather than swallowed.
        Assert.False(GCToOSInterface.VirtualRelease(region, 0));

        Assert.True(GCToOSInterface.VirtualRelease(region, 2 * pageSize));
        Assert.Equal(2 * pageSize, GCToOSInterface.LastMunmap.length);
        Assert.True(GCToOSInterface.LastMunmap.addr == region);
    }

    [Fact]
    public void LargePagesAreRequestedFromTheKernelAndCommittedInOneStep()
    {
        nuint pageSize = PageSize;
        nuint size = 2 * pageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserveAndCommitLargePages(size);

        try
        {
            Assert.Equal(1, GCToOSInterface.MmapCount);
            Assert.Equal(MAP_ANON | MAP_PRIVATE | LargePagesFlag, GCToOSInterface.LastMmap.flags);

            // The reservation is committing, so it is never advised out of the coredump, and
            // the memory it commits is new, so it is never advised back in either.
            Assert.Equal(0, GCToOSInterface.MadviseCount);

            // Huge pages are usually not configured, in which case the mapping fails and the
            // C++ still runs the commit against the null pointer, which fails in turn.
            if (region == null)
            {
                Assert.Equal(1, GCToOSInterface.MprotectCount);
                Assert.True(GCToOSInterface.LastMprotect.addr == null);
                Assert.NotEqual(0, GCToOSInterface.LastMprotect.result);
            }
            else
            {
                Assert.Equal(PROT_READ_WRITE, GCToOSInterface.LastMprotect.arg);
                region[0] = 1;
                Assert.Equal(1, region[0]);
            }
        }
        finally
        {
            if (region != null)
            {
                GCToOSInterface.VirtualRelease(region, size);
            }
        }
    }
#else
    //
    // The Windows flag translation. The expected values are the <windows.h> ones.
    //

    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_DECOMMIT = 0x00004000;
    private const uint MEM_RELEASE = 0x00008000;
    private const uint MEM_RESET = 0x00080000;
    private const uint MEM_WRITE_WATCH = 0x00200000;
    private const uint PAGE_READWRITE = 0x04;

    [Fact]
    public void GetPageSizeIsTheFixedWindowsPageSize()
    {
        Assert.Equal((nuint)4096, GCToOSInterface.GetPageSize());
    }

    [Fact]
    public void ReserveRequestsReadWriteAddressSpaceAndIgnoresTheAlignment()
    {
        nuint size = 4 * PageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0x10000, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.Equal(1, GCToOSInterface.VirtualAllocCount);
            Assert.False(GCToOSInterface.LastVirtualAlloc.numaAware);
            Assert.True(GCToOSInterface.LastVirtualAlloc.lpAddress == null);
            Assert.Equal(size, GCToOSInterface.LastVirtualAlloc.dwSize);
            Assert.Equal(MEM_RESERVE, GCToOSInterface.LastVirtualAlloc.flAllocationType);
            Assert.Equal(PAGE_READWRITE, GCToOSInterface.LastVirtualAlloc.flProtect);

            // Windows returns allocation-granularity aligned address space of its own accord.
            Assert.Equal((nuint)0, (nuint)region & 0xFFFF);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void ReserveAddsTheWriteWatchFlagWhenAsked()
    {
        nuint size = 4 * PageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.WriteWatch);

        Assert.Equal(MEM_RESERVE | MEM_WRITE_WATCH, GCToOSInterface.LastVirtualAlloc.flAllocationType);

        if (region != null)
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void ReserveAndCommitTakeTheNumaPathOnlyWhenANodeIsRequested()
    {
        nuint size = 4 * PageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None, 0);

        // VirtualAllocExNuma fails on a machine with no such node, which is not what is being
        // checked here: the port must have chosen the NUMA entry point at all.
        Assert.True(GCToOSInterface.LastVirtualAlloc.numaAware);
        Assert.Equal(0u, GCToOSInterface.LastVirtualAlloc.nndPreferred);

        if (region != null)
        {
            GCToOSInterface.ResetRecording();
            GCToOSInterface.VirtualCommit(region, PageSize, 0);
            Assert.True(GCToOSInterface.LastVirtualAlloc.numaAware);
            Assert.Equal(MEM_COMMIT, GCToOSInterface.LastVirtualAlloc.flAllocationType);

            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void CommitDecommitResetAndReleaseUseTheirOwnFlags()
    {
        nuint pageSize = PageSize;
        nuint size = 2 * pageSize;

        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));
        Assert.False(GCToOSInterface.LastVirtualAlloc.numaAware);
        Assert.True(GCToOSInterface.LastVirtualAlloc.lpAddress == region);
        Assert.Equal(MEM_COMMIT, GCToOSInterface.LastVirtualAlloc.flAllocationType);
        Assert.Equal(PAGE_READWRITE, GCToOSInterface.LastVirtualAlloc.flProtect);

        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualReset(region, pageSize, false));
        Assert.Equal(MEM_RESET, GCToOSInterface.LastVirtualAlloc.flAllocationType);
        Assert.Equal(0, GCToOSInterface.VirtualUnlockCount);

        // Only the unlocking form touches VirtualUnlock, and only after a successful reset.
        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualReset(region, pageSize, true));
        Assert.Equal(1, GCToOSInterface.VirtualUnlockCount);

        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualDecommit(region, pageSize));
        Assert.Equal(MEM_DECOMMIT, GCToOSInterface.LastVirtualFree.dwFreeType);
        Assert.Equal(pageSize, GCToOSInterface.LastVirtualFree.dwSize);

        // A release always passes a zero size, which is what MEM_RELEASE requires.
        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualRelease(region, size));
        Assert.Equal(MEM_RELEASE, GCToOSInterface.LastVirtualFree.dwFreeType);
        Assert.Equal((nuint)0, GCToOSInterface.LastVirtualFree.dwSize);
        Assert.True(GCToOSInterface.LastVirtualFree.lpAddress == region);
    }
#endif
}
