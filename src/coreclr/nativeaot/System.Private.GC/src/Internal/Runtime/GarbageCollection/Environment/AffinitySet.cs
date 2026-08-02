// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the AffinitySet class of gcenv.os.h: a set of processor indices, stored as a bitset of
// pointer-sized words.
//
// The layout is that of the C++ class -- the bitset pointer followed by its length in words --
// because GCToOSInterface::SetGCThreadsAffinitySet takes one of these and returns the platform
// layer's own, so both sides read each other's instances until that method is ported.
//
// The C++ class is a value with a destructor; a C# struct has none, so ~AffinitySet becomes an
// explicit Destroy() that the owner calls. The storage itself comes from the same nothrow heap
// allocation the C++ `new (nothrow) uintptr_t[]` performs, reached through a shim, because the
// managed GC has no allocator of its own to take it from.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// A set of processor indices used to store affinity.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AffinitySet
    {
        private const string RuntimeLibrary = "*";

        private static nuint BitsPerBitsetEntry => 8 * (nuint)sizeof(nuint);

        private nuint* m_bitset;
        private nuint m_bitsetDataSize;

        private static nuint GetBitsetEntryMask(nuint cpuIndex)
        {
            return (nuint)1 << (int)(cpuIndex & (BitsPerBitsetEntry - 1));
        }

        private static nuint GetBitsetEntryIndex(nuint cpuIndex)
        {
            return cpuIndex / BitsPerBitsetEntry;
        }

        public bool Initialize(int cpuCount)
        {
            Debug.Assert(m_bitset == null);

            m_bitsetDataSize = ((nuint)cpuCount + BitsPerBitsetEntry - 1) / BitsPerBitsetEntry;
            m_bitset = (nuint*)ManagedGC_AllocZeroed((nuint)sizeof(nuint) * m_bitsetDataSize);
            if (m_bitset == null)
            {
                m_bitsetDataSize = 0;
                return false;
            }

            // The C++ version memsets after `new`; the allocation shim returns zeroed memory.
            return true;
        }

        /// <summary>
        /// Releases the bitset. Stands in for <c>~AffinitySet</c>, which a C# struct cannot have.
        /// </summary>
        public void Destroy()
        {
            ManagedGC_Free(m_bitset);
            m_bitset = null;
            m_bitsetDataSize = 0;
        }

        public nuint* GetBitsetData()
        {
            return m_bitset;
        }

        /// <summary>Check if the set contains a processor.</summary>
        public readonly bool Contains(nuint cpuIndex)
        {
            Debug.Assert(GetBitsetEntryIndex(cpuIndex) < m_bitsetDataSize);
            return (m_bitset[GetBitsetEntryIndex(cpuIndex)] & GetBitsetEntryMask(cpuIndex)) != 0;
        }

        /// <summary>Add a processor to the set.</summary>
        public readonly void Add(nuint cpuIndex)
        {
            Debug.Assert(GetBitsetEntryIndex(cpuIndex) < m_bitsetDataSize);
            m_bitset[GetBitsetEntryIndex(cpuIndex)] |= GetBitsetEntryMask(cpuIndex);
        }

        /// <summary>Remove a processor from the set.</summary>
        public readonly void Remove(nuint cpuIndex)
        {
            Debug.Assert(GetBitsetEntryIndex(cpuIndex) < m_bitsetDataSize);
            m_bitset[GetBitsetEntryIndex(cpuIndex)] &= ~GetBitsetEntryMask(cpuIndex);
        }

        /// <summary>Check if the set is empty.</summary>
        public readonly bool IsEmpty()
        {
            for (nuint i = 0; i < m_bitsetDataSize; i++)
            {
                if (m_bitset[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Return the capacity of the affinity set (maximum number of processor indices it can
        /// hold).
        /// </summary>
        public readonly nuint MaxCpuCount()
        {
            return m_bitsetDataSize * BitsPerBitsetEntry;
        }

        /// <summary>Return the number of processors in the affinity set.</summary>
        public readonly nuint Count()
        {
            nuint count = 0;
            for (nuint i = 0; i < m_bitsetDataSize * BitsPerBitsetEntry; i++)
            {
                if (Contains(i))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Points the set at storage the caller owns, instead of allocating.
        /// </summary>
        /// <remarks>
        /// Not a member of the C++ class. It exists so that the bit manipulation above can be
        /// driven by tests without a runtime under it; the GC itself uses
        /// <see cref="Initialize(int)"/>.
        /// </remarks>
        internal void InitializeWithStorage(nuint* bitset, nuint bitsetDataSize)
        {
            m_bitset = bitset;
            m_bitsetDataSize = bitsetDataSize;
        }

        /// <summary>
        /// Stands in for <c>new (nothrow) uintptr_t[]</c>. See
        /// <c>nativeaot/Runtime/gcenv.managed.cpp</c>.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_AllocZeroed")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* ManagedGC_AllocZeroed(nuint size);

        /// <summary>Stands in for <c>delete[]</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Free")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_Free(void* memory);
    }
}
