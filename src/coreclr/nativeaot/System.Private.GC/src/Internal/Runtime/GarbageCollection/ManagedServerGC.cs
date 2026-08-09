// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Foundational active x64 Linux SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS port from
// gcinternal.h, init.cpp, interface.cpp, allocation.cpp, dynamic_heap_count.cpp, and gc.cpp.
// Collection entry points deliberately remain unrouted until the parallel mark/plan closure is
// present. Startup, heap selection, per-heap allocation, worker synchronization, the server
// t_join barrier, the gc_done_event collection handshake, and teardown are real server paths
// and do not forward through the workstation heap.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981

[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Explicit)]
internal unsafe struct MethodTable
{
    public const uint HasCriticalFinalizerFlag = 0x00000002;
    public const uint HasFinalizerFlag = 0x00100000;
    public const uint HasPointersFlag = 0x01000000;

    [System.Runtime.InteropServices.FieldOffset(0)]
    public ushort m_usComponentSize;
    [System.Runtime.InteropServices.FieldOffset(0)]
    public uint m_uFlags;
    [System.Runtime.InteropServices.FieldOffset(4)]
    public uint m_uBaseSize;

    public uint GetBaseSize() => m_uBaseSize;
    public ushort RawGetComponentSize() => m_usComponentSize;
    public int HasComponentSize() => (int)m_uFlags < 0 ? 1 : 0;
    public int HasReferenceFields() =>
        (m_uFlags & HasPointersFlag) != 0 ? 1 : 0;
    public int HasFinalizer() =>
        (m_uFlags & HasFinalizerFlag) != 0 ? 1 : 0;
    public int HasCriticalFinalizer() =>
        HasComponentSize() == 0 &&
        (m_uFlags & HasCriticalFinalizerFlag) != 0 ? 1 : 0;
    public int ContainsGCPointers() => HasReferenceFields();
    public int ContainsGCPointersOrCollectible() => HasReferenceFields();
}

[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct ObjHeader
{
    public const uint BIT_SBLK_GC_RESERVE = 0x20000000;
    public const uint BIT_SBLK_FINALIZER_RUN = 0x40000000;

    private uint m_uAlignpad;
    private uint m_uSyncBlockValue;

    public uint GetBits() => m_uSyncBlockValue;
    public void SetGCBit() =>
        m_uSyncBlockValue |= BIT_SBLK_GC_RESERVE;
    public void SetFinalizerRun() =>
        m_uSyncBlockValue |= BIT_SBLK_FINALIZER_RUN;
    public void ClrFinalizerRun() =>
        m_uSyncBlockValue &= ~BIT_SBLK_FINALIZER_RUN;
}

[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
internal unsafe struct CObjectHeader
{
    private const nuint SPECIAL_HEADER_BITS = 0x7;
    public const nuint GC_MARKED = 0x1;
    private MethodTable* m_pEEType;

    public MethodTable* RawGetMethodTable() => m_pEEType;

    public void RawSetMethodTable(MethodTable* methodTable) => m_pEEType = methodTable;

    public MethodTable* GetMethodTable() =>
        (MethodTable*)((nuint)m_pEEType & ~SPECIAL_HEADER_BITS);

    public int IsMarked() => ((nuint)m_pEEType & GC_MARKED) != 0 ? 1 : 0;

    public void SetMarked() =>
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() | GC_MARKED));

    public void ClearMarked() =>
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() & ~GC_MARKED));

    public void SetPinned() => GetHeader()->SetGCBit();

    public int IsPinned() =>
        (GetHeader()->GetBits() & ObjHeader.BIT_SBLK_GC_RESERVE) != 0 ? 1 : 0;

    public int ContainsGCPointers() => GetMethodTable()->ContainsGCPointers();

    public int ContainsGCPointersOrCollectible() =>
        GetMethodTable()->ContainsGCPointersOrCollectible();

    public int IsFree() =>
        GetMethodTable() ==
        (MethodTable*)GCCommon.g_gc_pFreeObjectMethodTable ? 1 : 0;

    public ObjHeader* GetHeader()
    {
        fixed (CObjectHeader* header = &this)
        {
            return (ObjHeader*)((byte*)header - sizeof(nuint));
        }
    }

    public static uint GetNumComponents(CObjectHeader* header) =>
        *(uint*)((byte*)header + sizeof(nuint));
}

internal unsafe partial struct mark_queue_t
{
    [InlineArray(16)]
    internal struct slot_table_t
    {
        private nuint _element0;
    }

    public slot_table_t slot_table;
    public nuint curr_slot_index;
}


internal enum gc_dynamic_adaptation_mode
{
    dynamic_adaptation_default = 0,
    dynamic_adaptation_to_application_sizes = 1,
}

internal enum gc_join_flavor
{
    join_flavor_server_gc = 0,
    join_flavor_bgc = 1,
}

internal enum hc_record_stage
{
    hc_record_set_last_heaps = 0,
    hc_record_before_check_timeout = 1,
    hc_record_before_check_gc_start = 2,
    hc_record_change_done = 3,
    hc_record_still_active = 4,
    hc_record_became_active = 5,
    hc_record_became_inactive = 6,
    hc_record_inactive_waiting = 7,
    hc_record_check_cancelled_prep = 8,
    hc_record_check_cancelled_bgc = 9,
    hc_record_bgc_active = 10,
    hc_record_bgc_inactive = 11,
}

internal struct hc_history
{
    public nuint gc_index;
    public short stage;
    public short last_n_heaps;
    public short n_heaps;
    public short new_n_heaps;
    public short idle_thread_count;
    public short gc_t_join_n_threads;
    public short gc_t_join_join_lock;
    public short bgc_t_join_n_threads;
    public int bgc_thread_os_id;
    public short bgc_t_join_join_lock;
    public byte gc_t_join_joined_p;
    public byte bgc_t_join_joined_p;
    public byte concurrent_p;
    public byte bgc_thread_running;
}

internal unsafe struct dynamic_heap_count_data_t
{
    public const int sample_size = 3;

    internal struct sample
    {
        public ulong elapsed_between_gcs;
        public ulong gc_pause_time;
        public ulong msl_wait_time;
        public nuint gc_index;
        public nuint gc_survived_size;
        public int gen0_budget_per_heap;
    }

    [InlineArray(sample_size)]
    internal struct sample_array
    {
        private sample _element0;
    }

    public float target_tcp;
    public float target_gen2_tcp;
    public float gen0_growth_soh_ratio_percent;
    public float gen0_growth_soh_ratio_min;
    public float gen0_growth_soh_ratio_max;
    public uint sample_index;
    public sample_array samples;
    public nuint current_samples_count;
    public nuint processed_samples_count;
    public nuint current_gen2_samples_count;
    public nuint processed_gen2_samples_count;
    public int last_n_heaps;
    public int new_n_heaps;
    public int idle_thread_count;
    public byte should_change_heap_count;
    public byte init_only_p;

    public void initialize(int heapCount)
    {
        target_tcp = 2.0f;
        target_gen2_tcp = 10.0f;
        gen0_growth_soh_ratio_percent = 1.0f;
        gen0_growth_soh_ratio_min = 0.1f;
        gen0_growth_soh_ratio_max = 10.0f;
        last_n_heaps = heapCount;
        new_n_heaps = heapCount;
    }
}

// first_thread_arrived is the index of the third join event, used only by r_join/r_restart.
internal static class join_constants
{
    public const int first_thread_arrived = 2;
}

internal enum join_type
{
    type_last_join = 0,
    type_join = 1,
    type_restart = 2,
    type_first_r_join = 3,
    type_r_join = 4,
}

internal enum join_time
{
    time_start = 0,
    time_end = 1,
}

internal enum join_heap_index
{
    join_heap_restart = 100,
    join_heap_r_restart = 200,
}

// gcinternal.h gc_join_stage. These name the collection-phase join points the server GC threads
// synchronize on through gc_t_join.join / r_join. The values are load-bearing: they index the
// GCJoin ETW payload and must match the native enum exactly. Only a subset is currently reached
// by translated code; the rest are present so later parallel-phase slices can reference them
// without renumbering.
internal enum gc_join_stage
{
    gc_join_init_cpu_mapping = 0,
    gc_join_done = 1,
    gc_join_generation_determined = 2,
    gc_join_begin_mark_phase = 3,
    gc_join_scan_dependent_handles = 4,
    gc_join_rescan_dependent_handles = 5,
    gc_join_scan_sizedref_done = 6,
    gc_join_null_dead_short_weak = 7,
    gc_join_scan_finalization = 8,
    gc_join_null_dead_long_weak = 9,
    gc_join_null_dead_syncblk = 10,
    gc_join_decide_on_compaction = 11,
    gc_join_rearrange_segs_compaction = 12,
    gc_join_adjust_handle_age_compact = 13,
    gc_join_adjust_handle_age_sweep = 14,
    gc_join_begin_relocate_phase = 15,
    gc_join_relocate_phase_done = 16,
    gc_join_verify_objects_done = 17,
    gc_join_start_bgc = 18,
    gc_join_restart_ee = 19,
    gc_join_concurrent_overflow = 20,
    gc_join_suspend_ee = 21,
    gc_join_bgc_after_ephemeral = 22,
    gc_join_allow_fgc = 23,
    gc_join_bgc_sweep = 24,
    gc_join_suspend_ee_verify = 25,
    gc_join_restart_ee_verify = 26,
    gc_join_set_state_free = 27,
    gc_r_join_update_card_bundle = 28,
    gc_join_after_absorb = 29,
    gc_join_verify_copy_table = 30,
    gc_join_after_reset = 31,
    gc_join_after_ephemeral_sweep = 32,
    gc_join_after_profiler_heap_walk = 33,
    gc_join_minimal_gc = 34,
    gc_join_after_commit_soh_no_gc = 35,
    gc_join_expand_loh_no_gc = 36,
    gc_join_final_no_gc = 37,
    // No longer in use but do not remove, see comments for this enum.
    gc_join_disable_software_write_watch = 38,
    gc_join_merge_temp_fl = 39,
    gc_join_bridge_processing = 40,
    gc_join_max = 41,
}

// join_structure of gcinternal.h. The DECLSPEC_ALIGN(HS_CACHE_LINE_SIZE) separators of the
// native struct are false-sharing avoidance and are not observable through the DAC, so the C#
// port keeps the members in their declared order without the explicit cache-line padding. The
// Volatile<>/VOLATILE() fields are plain integers accessed through System.Threading.Volatile,
// matching the port's convention for the gcpriv.h volatile-wrapped fields.
internal unsafe struct join_structure
{
    public int n_threads;

    [InlineArray(3)]
    internal struct joined_event_array
    {
        private GCEvent _element0;
    }

    // The last event in the array is only used for first_thread_arrived.
    public joined_event_array joined_event;
    public int lock_color;
    public int wait_done;
    public int joined_p;
    public int join_lock;
    public int r_join_lock;
}

// t_join of gcinternal.h. JOIN_STATS instrumentation is not built and is omitted.
internal unsafe struct t_join
{
    private join_structure join_struct;

    private int id;
    private gc_join_flavor flavor;

    public bool init(int n_th, gc_join_flavor f)
    {
        join_struct.n_threads = n_th;
        join_struct.lock_color = 0;
        for (int i = 0; i < 3; i++)
        {
            if (!join_struct.joined_event[i].IsValid())
            {
                join_struct.joined_p = 0;
                if (!join_struct.joined_event[i].CreateManualEventNoThrow(initialState: false))
                {
                    return false;
                }
            }
        }
        join_struct.join_lock = join_struct.n_threads;
        join_struct.r_join_lock = join_struct.n_threads;
        join_struct.wait_done = 0;
        flavor = f;

        return true;
    }

    public void update_n_threads(int n_th)
    {
        join_struct.n_threads = n_th;
        join_struct.join_lock = n_th;
        join_struct.r_join_lock = n_th;
    }

    public int get_num_threads() => join_struct.n_threads;

    // This is for instrumentation only.
    public int get_join_lock() =>
        Volatile.Read(ref join_struct.join_lock);

    public void destroy()
    {
        for (int i = 0; i < 3; i++)
        {
            if (join_struct.joined_event[i].IsValid())
            {
                join_struct.joined_event[i].CloseEvent();
            }
        }
    }

    private static void fire_event(int heap, join_time time, join_type type, int join_id) =>
        GCEvents.GCEventFireGCJoin_V2(
            (uint)heap,
            (uint)time,
            (uint)type,
            (uint)join_id);

    public void join(gc_heap* gch, int join_id)
    {
        Debug.Assert(Volatile.Read(ref join_struct.joined_p) == 0);
        int color = Volatile.Read(ref join_struct.lock_color);

        if (Interlocked.Decrement(ref join_struct.join_lock) != 0)
        {
            fire_event(gch->heap_number, join_time.time_start, join_type.type_join, join_id);

            //busy wait around the color
            if (color == Volatile.Read(ref join_struct.lock_color))
            {
            respin:
                int spin_count = 128 * (int)gc_heap.yp_spin_count_unit;
                for (int j = 0; j < spin_count; j++)
                {
                    if (color != Volatile.Read(ref join_struct.lock_color))
                    {
                        break;
                    }
                    GCEnv.YieldProcessor();           // indicate to the processor that we are spinning
                }

                // we've spun, and if color still hasn't changed, fall into hard wait
                if (color == Volatile.Read(ref join_struct.lock_color))
                {
                    uint dwJoinWait =
                        join_struct.joined_event[color].Wait(GCEnv.INFINITE, alertable: false);

                    if (dwJoinWait != GCEnv.WAIT_OBJECT_0)
                    {
                        FATAL_GC_ERROR();
                    }
                }

                // avoid race due to the thread about to reset the event (occasionally) being preempted before ResetEvent()
                if (color == Volatile.Read(ref join_struct.lock_color))
                {
                    goto respin;
                }
            }

            fire_event(gch->heap_number, join_time.time_end, join_type.type_join, join_id);
        }
        else
        {
            fire_event(gch->heap_number, join_time.time_start, join_type.type_last_join, join_id);

            Volatile.Write(ref join_struct.joined_p, 1);
            join_struct.joined_event[color == 0 ? 1 : 0].Reset();
            id = join_id;
        }
    }

    // Reverse join - first thread gets here does the work; other threads will only proceed
    // after the work is done.
    // Note that you cannot call this twice in a row on the same thread. Plus there's no
    // need to call it twice in row - you should just merge the work.
    public bool r_join(gc_heap* gch, int join_id)
    {
        if (join_struct.n_threads == 1)
        {
            return true;
        }

        if (Interlocked.CompareExchange(
                ref join_struct.r_join_lock,
                0,
                join_struct.n_threads) == 0)
        {
            fire_event(gch->heap_number, join_time.time_start, join_type.type_join, join_id);

            //busy wait around the color
        respin:
            int spin_count = 256 * (int)gc_heap.yp_spin_count_unit;
            for (int j = 0; j < spin_count; j++)
            {
                if (Volatile.Read(ref join_struct.wait_done) != 0)
                {
                    break;
                }
                GCEnv.YieldProcessor();           // indicate to the processor that we are spinning
            }

            // we've spun, and if color still hasn't changed, fall into hard wait
            if (Volatile.Read(ref join_struct.wait_done) == 0)
            {
                uint dwJoinWait = join_struct.joined_event[join_constants.first_thread_arrived]
                    .Wait(GCEnv.INFINITE, alertable: false);
                if (dwJoinWait != GCEnv.WAIT_OBJECT_0)
                {
                    FATAL_GC_ERROR();
                }
            }

            // avoid race due to the thread about to reset the event (occasionally) being preempted before ResetEvent()
            if (Volatile.Read(ref join_struct.wait_done) == 0)
            {
                goto respin;
            }

            fire_event(gch->heap_number, join_time.time_end, join_type.type_join, join_id);

            return false;
        }
        else
        {
            fire_event(gch->heap_number, join_time.time_start, join_type.type_first_r_join, join_id);
            return true;
        }
    }

    public void restart()
    {
        fire_event((int)join_heap_index.join_heap_restart, join_time.time_start, join_type.type_restart, -1);
        Debug.Assert(Volatile.Read(ref join_struct.joined_p) != 0);
        Volatile.Write(ref join_struct.joined_p, 0);
        join_struct.join_lock = join_struct.n_threads;
        int color = Volatile.Read(ref join_struct.lock_color);
        Volatile.Write(ref join_struct.lock_color, color == 0 ? 1 : 0);
        join_struct.joined_event[color].Set();

        fire_event((int)join_heap_index.join_heap_restart, join_time.time_end, join_type.type_restart, -1);
    }

    public bool joined() =>
        Volatile.Read(ref join_struct.joined_p) != 0;

    public void r_restart()
    {
        if (join_struct.n_threads != 1)
        {
            fire_event((int)join_heap_index.join_heap_r_restart, join_time.time_start, join_type.type_restart, -1);
            Volatile.Write(ref join_struct.wait_done, 1);
            join_struct.joined_event[join_constants.first_thread_arrived].Set();
            fire_event((int)join_heap_index.join_heap_r_restart, join_time.time_end, join_type.type_restart, -1);
        }
    }

    public void r_init()
    {
        if (join_struct.n_threads != 1)
        {
            join_struct.r_join_lock = join_struct.n_threads;
            Volatile.Write(ref join_struct.wait_done, 0);
            join_struct.joined_event[join_constants.first_thread_arrived].Reset();
        }
    }

    // gcpriv.h FATAL_GC_ERROR(): break, then report the fatal error to the EE. The dprintf /
    // _ASSERTE lines of the C++ helper have no counterpart in the port.
    private const uint COR_E_EXECUTIONENGINE = 0x80131506;

    private static void FATAL_GC_ERROR()
    {
        GCToOSInterface.DebugBreak();
        GCToEEInterface.HandleFatalError(COR_E_EXECUTIONENGINE);
    }
}

internal static unsafe class heap_select
{
    private static ushort* s_proc_no_to_heap_no;
    private static ushort* s_heap_no_to_proc_no;
    private static ushort* s_heap_no_to_numa_node;
    private static uint s_processorCount;
    private static int s_fallbackHeap;

    public static bool init(int heapCount)
    {
        s_processorCount = GCToOSInterface.GetMaxProcessorCount();
        s_proc_no_to_heap_no = (ushort*)SyncImports.ManagedGC_AllocZeroed(
            unchecked((nuint)s_processorCount * (nuint)sizeof(ushort)));
        s_heap_no_to_proc_no = (ushort*)SyncImports.ManagedGC_AllocZeroed(
            unchecked((nuint)heapCount * (nuint)sizeof(ushort)));
        s_heap_no_to_numa_node = (ushort*)SyncImports.ManagedGC_AllocZeroed(
            unchecked((nuint)heapCount * (nuint)sizeof(ushort)));
        if (s_proc_no_to_heap_no is null ||
            s_heap_no_to_proc_no is null ||
            s_heap_no_to_numa_node is null)
        {
            destroy();
            return false;
        }

        for (int heapNumber = 0; heapNumber < heapCount; heapNumber++)
        {
            ushort processor = 0;
            ushort node = GCToOSInterface.NUMA_NODE_UNDEFINED;
            if (!GCToOSInterface.GetProcessorForHeap((ushort)heapNumber, &processor, &node))
            {
                processor = (ushort)(heapNumber % s_processorCount);
                node = 0;
            }

            s_heap_no_to_proc_no[heapNumber] = processor;
            s_heap_no_to_numa_node[heapNumber] =
                node == GCToOSInterface.NUMA_NODE_UNDEFINED ? (ushort)0 : node;
            if (processor < s_processorCount)
            {
                s_proc_no_to_heap_no[processor] = (ushort)heapNumber;
            }
        }

        return true;
    }

    public static void destroy()
    {
        if (s_proc_no_to_heap_no is not null)
        {
            SyncImports.ManagedGC_Free(s_proc_no_to_heap_no);
        }
        if (s_heap_no_to_proc_no is not null)
        {
            SyncImports.ManagedGC_Free(s_heap_no_to_proc_no);
        }
        if (s_heap_no_to_numa_node is not null)
        {
            SyncImports.ManagedGC_Free(s_heap_no_to_numa_node);
        }

        s_proc_no_to_heap_no = null;
        s_heap_no_to_proc_no = null;
        s_heap_no_to_numa_node = null;
        s_processorCount = 0;
        s_fallbackHeap = 0;
    }

    public static void init_cpu_mapping(int heapNumber)
    {
        if (!GCToOSInterface.CanGetCurrentProcessorNumber())
        {
            return;
        }

        uint processor = GCToOSInterface.GetCurrentProcessorNumber();
        if (processor < s_processorCount)
        {
            s_proc_no_to_heap_no[processor] = (ushort)heapNumber;
        }
    }

    public static int select_heap(gc_alloc_context* acontext)
    {
        _ = acontext;
        int heapNumber;
        if (GCToOSInterface.CanGetCurrentProcessorNumber())
        {
            uint processor = GCToOSInterface.GetCurrentProcessorNumber();
            heapNumber = processor < s_processorCount
                ? s_proc_no_to_heap_no[processor]
                : 0;
        }
        else
        {
            heapNumber = Interlocked.Increment(ref s_fallbackHeap);
        }

        int heapCount = gc_heap.n_heaps;
        return heapCount == 0 ? 0 : (int)((uint)heapNumber % (uint)heapCount);
    }

    public static ushort find_proc_no_from_heap_no(int heapNumber) =>
        s_heap_no_to_proc_no[heapNumber];

    public static ushort find_numa_node_from_heap_no(int heapNumber) =>
        s_heap_no_to_numa_node[heapNumber];
}

#pragma warning disable CS8981
internal unsafe partial struct gc_heap
#pragma warning restore CS8981
{
    public static int n_heaps;
    public static int n_max_heaps;
    public static gc_heap** g_heaps;
    public static int dynamic_adaptation_mode;
    public static dynamic_heap_count_data_t dynamic_heap_count_data;
    public static GCEvent gc_start_event;
    public static GCEvent ee_suspend_event;
    public static t_join gc_t_join;
    // gc_started / internal_gc_done are PER_HEAP_ISOLATED in gcpriv.h; they gate the
    // gc_done_event handshake mutators observe while a collection is in flight.
    public static int gc_started;
    public static bool internal_gc_done;
    public static int server_gc_shutdown;
    public static int server_gc_threads_created;
    public static int server_gc_threads_exited;
    public static bool gc_thread_no_affinitize_p;
    public static nuint min_gen0_balance_delta;
    public static nuint min_balance_threshold;
    public static nuint max_decommit_step_size;
    public static volatile int gradual_decommit_in_progress_p;

    private static byte s_build_variant;
    private static byte s_built_with_svr;
    private static uint s_max_generation;
    private static GcDacVars* s_dac_vars;
    public static bool gc_can_use_concurrent;
    public static nuint physical_memory_from_config;
    public static nuint gen0_min_budget_from_config;
    public static nuint gen0_max_budget_from_config;
    public static uint high_mem_percent_from_config;
    public static byte use_large_pages_p;
    public static byte use_frozen_segments_p;
    public static oom_history oom_info;

    public static generation* generation_table_of(gc_heap* heap) =>
        &heap->generation_table0;

    public static dynamic_data* dynamic_data_of(gc_heap* heap, int generationNumber) =>
        &heap->dynamic_data_table0 + generationNumber;

    public static nuint Align(nuint bytes, int alignment) =>
        unchecked((bytes + (nuint)alignment - 1) & ~((nuint)alignment - 1));

    public static nuint Align(nuint bytes) =>
        Align(bytes, sizeof(byte*));

    // gc.cpp AlignQword: rounds UOH object sizes up to an 8-byte boundary. FEATURE_STRUCTALIGN is
    // not defined for this port, so this is the plain 8-byte round-up used by the card scan's LOH/
    // POH object walk.
    public static nuint AlignQword(nuint nbytes) =>
        unchecked((nbytes + 7) & ~(nuint)7);

    public static int get_alignment_constant(bool small_object_p)
    {
        _ = small_object_p;
        return sizeof(byte*);
    }

    public static nuint size(byte* obj)
    {
        CObjectHeader* header = (CObjectHeader*)obj;
        MethodTable* methodTable = header->GetMethodTable();
        nuint objectSize = methodTable->GetBaseSize();
        if (methodTable->HasComponentSize() != 0)
        {
            objectSize = unchecked(
                objectSize +
                ((nuint)CObjectHeader.GetNumComponents(header) *
                 methodTable->RawGetComponentSize()));
        }
        return objectSize;
    }

    public static void make_unused_array(
        byte* address,
        nuint size,
        int clearp = 0,
        int resetp = 0)
    {
        _ = clearp;
        _ = resetp;
        if (size < Align((nuint)GCInterfaceOffsets.min_obj_size))
        {
            return;
        }
        *(nuint*)address = (nuint)GCCommon.g_gc_pFreeObjectMethodTable;
        if (size >=
            (nuint)GCInterfaceOffsets.min_obj_size + (nuint)sizeof(nuint))
        {
            *(nuint*)(address + sizeof(nuint)) =
                (size - (nuint)GCInterfaceOffsets.min_obj_size) /
                (nuint)sizeof(nuint);
        }
    }

    public static byte* pinned_plug(mark* entry) =>
        entry->first + (nint)entry->len;

    public static void initialize_loh_pinned_queue_state()
    {
        loh_pinned_queue_tos = 0;
        loh_pinned_queue_bos = 0;
        loh_pinned_queue_length = 0;
        loh_pinned_queue_decay = LOH_PIN_DECAY;
        loh_pinned_queue = null;
    }

    public static void initialize_concurrent_gc()
    {
        gc_can_use_concurrent = false;
    }

    public static void initialize_mark_phase_state()
    {
        // The MULTIPLE_HEAPS mark-queue/pinned-stack/overflow state is gcpriv.h
        // PER_HEAP_FIELD_SINGLE_GC, so it is reset per heap by the gc_heap* overload in
        // ManagedServerGCMarkPhase.cs rather than through this shared static entry point.
    }

    public static void initialize_server_allocation_state(gc_heap* heap, int heapNumber)
    {
        generation* generationTable = generation_table_of(heap);
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(generationTable + i);
        }

        heap->gen2_alloc_list = default;
        *generation.generation_allocator(generation_of(
            generationTable,
            (int)gc_generation_num.soh_gen2)) =
            new allocator(
                12,
                7,
                (alloc_list*)Unsafe.AsPointer(ref heap->gen2_alloc_list[0]),
                (int)gc_generation_num.soh_gen2);
        heap->loh_alloc_list = default;
        *generation.generation_allocator(generation_of(
            generationTable,
            (int)gc_generation_num.loh_generation)) =
            new allocator(
                7,
                15,
                (alloc_list*)Unsafe.AsPointer(ref heap->loh_alloc_list[0]));
        heap->poh_alloc_list = default;
        *generation.generation_allocator(generation_of(
            generationTable,
            (int)gc_generation_num.poh_generation)) =
            new allocator(
                19,
                7,
                (alloc_list*)Unsafe.AsPointer(ref heap->poh_alloc_list[0]));

        GCSpinLock.initialize(&heap->more_space_lock_soh);
        GCSpinLock.initialize(&heap->more_space_lock_uoh);
        init_dynamic_data_for_server(heap);
        heap->allocation_quantum = 32 * 1024;
        heap->heap_number = heapNumber;
        heap->server_free_regions = default;
        heap->alloc_context_count = 0;
        heap->gc_done_event_lock = -1;
        heap->gc_done_event_set = false;
        heap->condemned_generation_num = 0;
        heap->blocking_collection = 0;
        heap->elevation_requested = 0;
        heap->generation_skip_ratio = 100;
        heap->last_gc_before_oom = 0;
        heap->gen_to_condemn_reasons = default;
        initialize_mark_phase_state(heap);
    }

    private static void init_dynamic_data_for_server(gc_heap* heap)
    {
        ulong now = GCCommon.GetHighPrecisionTimeStamp();
        generation* generationTable = generation_table_of(heap);
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            dynamic_data* data = dynamic_data_of(heap, i);
            *data = default;
            data->min_size = i == 0 ? 256 * 1024u : 3 * 1024 * 1024u;
            data->gc_clock = 0;
            data->time_clock = now;
            data->previous_time_clock = now;
            data->new_allocation = unchecked((nint)data->min_size);
            data->gc_new_allocation = data->new_allocation;
            data->desired_allocation = unchecked((nuint)data->new_allocation);
        }
    }

    public static gc_heap* heap_of_context(gc_alloc_context* context)
    {
        gc_heap* heap = (gc_heap*)context->gc_reserved_1;
        if (heap is not null && heap->heap_number < n_heaps)
        {
            return heap;
        }

        int heapNumber = heap_select.select_heap(context);
        heap = g_heaps[heapNumber];
        context->gc_reserved_1 = heap;
        context->gc_reserved_2 = heap;
        Interlocked.Increment(ref heap->alloc_context_count);
        return heap;
    }

    public static void gc_thread_stub(void* argument)
    {
        gc_heap* heap = (gc_heap*)argument;
        if (!gc_thread_no_affinitize_p)
        {
            GCToOSInterface.SetThreadAffinity(
                heap_select.find_proc_no_from_heap_no(heap->heap_number));
        }

        GCToOSInterface.BoostThreadPriority();
        heap_select.init_cpu_mapping(heap->heap_number);
        while (Volatile.Read(ref server_gc_shutdown) == 0)
        {
            gc_start_event.Wait(GCEnv.INFINITE, alertable: false);
        }

        Interlocked.Increment(ref server_gc_threads_exited);
    }

    public static bool create_thread_support(int heapCount)
    {
        if (!gc_start_event.CreateManualEventNoThrow(initialState: false) ||
            !ee_suspend_event.CreateAutoEventNoThrow(initialState: false) ||
            !gc_t_join.init(heapCount, gc_join_flavor.join_flavor_server_gc))
        {
            destroy_thread_support();
            return false;
        }

        return true;
    }

    public static void destroy_thread_support()
    {
        gc_t_join.destroy();
        if (ee_suspend_event.IsValid())
        {
            ee_suspend_event.CloseEvent();
        }
        if (gc_start_event.IsValid())
        {
            gc_start_event.CloseEvent();
        }
    }

    public static bool enable_preemptive() =>
        GCToEEInterface.EnablePreemptiveGC() != 0;

    public static void disable_preemptive(bool restore_cooperative)
    {
        if (restore_cooperative)
        {
            GCToEEInterface.DisablePreemptiveGC();
        }
    }

    public static uint wait_for_gc_done(int timeOut = unchecked((int)GCEnv.INFINITE))
    {
        bool cooperative_mode = enable_preemptive();

        uint dwWaitResult = 0;

        gc_heap* wait_heap = null;
        while (Volatile.Read(ref gc_started) != 0)
        {
            wait_heap = g_heaps[heap_select.select_heap(null)];
            dwWaitResult = wait_heap->gc_done_event.Wait((uint)timeOut, alertable: false);
        }
        disable_preemptive(cooperative_mode);

        return dwWaitResult;
    }

    public static void set_gc_done(gc_heap* heap)
    {
        enter_gc_done_event_lock(heap);
        if (!heap->gc_done_event_set)
        {
            heap->gc_done_event_set = true;
            heap->gc_done_event.Set();
        }
        exit_gc_done_event_lock(heap);
    }

    public static void reset_gc_done(gc_heap* heap)
    {
        enter_gc_done_event_lock(heap);
        if (heap->gc_done_event_set)
        {
            heap->gc_done_event_set = false;
            heap->gc_done_event.Reset();
        }
        exit_gc_done_event_lock(heap);
    }

    public static void enter_gc_done_event_lock(gc_heap* heap)
    {
        uint dwSwitchCount = 0;
    retry:

        if (Interlocked.CompareExchange(ref heap->gc_done_event_lock, 0, -1) >= 0)
        {
            while (Volatile.Read(ref heap->gc_done_event_lock) >= 0)
            {
                if (GCCommon.g_num_processors > 1)
                {
                    int spin_count = (int)yp_spin_count_unit;
                    for (int j = 0; j < spin_count; j++)
                    {
                        if (Volatile.Read(ref heap->gc_done_event_lock) < 0)
                        {
                            break;
                        }
                        GCEnv.YieldProcessor();           // indicate to the processor that we are spinning
                    }
                    if (Volatile.Read(ref heap->gc_done_event_lock) >= 0)
                    {
                        GCToOSInterface.YieldThread(++dwSwitchCount);
                    }
                }
                else
                {
                    GCToOSInterface.YieldThread(++dwSwitchCount);
                }
            }
            goto retry;
        }
    }

    public static void exit_gc_done_event_lock(gc_heap* heap)
    {
        Volatile.Write(ref heap->gc_done_event_lock, -1);
    }

    [RuntimeImport("*", "ManagedGC_CreateServerThread")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern int ManagedGC_CreateServerThread(
        delegate*<void*, void> threadStart,
        void* context,
        byte* name);

    public static bool create_gc_thread(gc_heap* heap)
    {
        fixed (byte* name = ".NET Server GC\0"u8)
        {
            bool created = ManagedGC_CreateServerThread(
                &gc_thread_stub,
                heap,
                name) != 0;
            if (created)
            {
                Interlocked.Increment(ref server_gc_threads_created);
            }
            return created;
        }
    }

    public static void PopulateDacVars(GcDacVars* gcDacVars)
    {
        Debug.Assert(gcDacVars is not null);
        gcDacVars->major_version_number = 2;
        gcDacVars->minor_version_number = 8;
        gcDacVars->generation_size = (nuint)sizeof(generation);
        gcDacVars->total_generation_count = (nuint)gc_generation_num.total_generation_count;
        s_build_variant =
            GCInterfaceDacConstants.build_variant_use_region |
            GCInterfaceDacConstants.build_variant_background_gc |
            GCInterfaceDacConstants.build_variant_dynamic_heap_count;
        s_built_with_svr = 1;
        s_max_generation = GCInterfaceOffsets.max_generation;
        s_dac_vars = gcDacVars;
        gcDacVars->build_variant = (byte*)Unsafe.AsPointer(ref s_build_variant);
        gcDacVars->built_with_svr = (byte*)Unsafe.AsPointer(ref s_built_with_svr);
        gcDacVars->max_gen = (uint*)Unsafe.AsPointer(ref s_max_generation);
        gcDacVars->n_heaps = (int*)Unsafe.AsPointer(ref n_heaps);
        fixed (gc_heap*** heaps = &g_heaps)
        {
            gcDacVars->g_heaps = (unused_gc_heap***)heaps;
        }
        gcDacVars->dynamic_adaptation_mode =
            (int*)Unsafe.AsPointer(ref dynamic_adaptation_mode);
        gcDacVars->total_bookkeeping_elements =
            (int)bookkeeping_element.total_bookkeeping_elements;
        gcDacVars->count_free_region_kinds =
            (int)free_region_kind.count_free_region_kinds;
        gcDacVars->card_table_info_size = (nuint)sizeof(card_table_info);
    }
}

internal static unsafe class ManagedGCRegionBootstrap
{
    private const int S_OK = 0;
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private static byte* s_reservedRegionRange;
    private static nuint s_reservedRegionRangeSize;
    private static bool s_initialized;

    public static int Prepare()
    {
        nuint regionSize = unchecked((nuint)GCConfig.GetGCRegionSize());
        if (regionSize >= gc_heap.MAX_REGION_SIZE)
        {
            return GCEnv.CLR_E_GC_BAD_REGION_SIZE;
        }
        if (regionSize == 0)
        {
            regionSize = gc_heap.DefaultMinSegmentSize;
        }
        if (!gc_heap.power_of_two_p(regionSize))
        {
            return E_OUTOFMEMORY;
        }

        gc_heap.global_region_allocator = default;
        gc_heap.initial_regions = null;
        gc_heap.initialize_min_segment_size_shr(regionSize);
        gc_heap.global_region_allocator.initialize();
        gc_heap.initialize_gc_lock();
        GCWriteBarrier.initialize();
        return S_OK;
    }

    public static bool Initialize()
    {
        if (s_initialized)
        {
            return true;
        }

        int processorCount = (int)GCToEEInterface.GetCurrentProcessCpuCount();
        int heapCount = processorCount > 0 ? processorCount : 1;
        long configuredHeapCount = GCConfig.GetHeapCount();
        if (configuredHeapCount > 0 && configuredHeapCount < heapCount)
        {
            heapCount = (int)configuredHeapCount;
        }
        if (heapCount > GCToOSInterface.MAX_SUPPORTED_HEAPS)
        {
            heapCount = GCToOSInterface.MAX_SUPPORTED_HEAPS;
        }

        long configuredMaxHeapCount = GCConfig.GetMaxHeapCount();
        if (configuredMaxHeapCount > 0 && configuredMaxHeapCount < heapCount)
        {
            heapCount = (int)configuredMaxHeapCount;
        }
        if (heapCount < 1)
        {
            heapCount = 1;
        }

        gc_heap.n_max_heaps = heapCount;
        gc_heap.n_heaps = heapCount;
        gc_heap.dynamic_adaptation_mode = configuredHeapCount == 0
            ? (int)GCConfig.GetGCDynamicAdaptationMode()
            : (int)gc_dynamic_adaptation_mode.dynamic_adaptation_default;
        gc_heap.dynamic_heap_count_data = default;
        gc_heap.dynamic_heap_count_data.initialize(heapCount);
        gc_heap.gc_thread_no_affinitize_p = GCConfig.GetNoAffinitize() != 0;

        if (!heap_select.init(heapCount) ||
            !gc_heap.create_thread_support(heapCount))
        {
            Cleanup();
            return false;
        }

        nuint regionSize = (nuint)1 << (int)gc_heap.min_segment_size_shr;
        nuint initialPerHeap = unchecked(
            ((nuint)3 + ((nuint)2 * region_allocator.LARGE_REGION_FACTOR)) *
            regionSize);
        nuint minimumRange = unchecked(initialPerHeap * (nuint)heapCount);
        nuint configuredRange = unchecked((nuint)GCConfig.GetGCRegionRange());
        nuint rangeSize = configuredRange > minimumRange
            ? configuredRange
            : unchecked(minimumRange * 2);
        rangeSize = unchecked((rangeSize + regionSize - 1) & ~(regionSize - 1));
        GCConfig.SetGCRegionRange((long)rangeSize);

        s_reservedRegionRange = GCToOSInterface.VirtualReserve(
            rangeSize,
            regionSize,
            (uint)VirtualReserveFlags.None);
        if (s_reservedRegionRange is null)
        {
            Cleanup();
            return false;
        }
        s_reservedRegionRangeSize = rangeSize;

        fixed (byte** lowest = &GCCommon.g_gc_lowest_address)
        fixed (byte** highest = &GCCommon.g_gc_highest_address)
        {
            if (!gc_heap.global_region_allocator.init(
                s_reservedRegionRange,
                s_reservedRegionRange + (nint)rangeSize,
                regionSize,
                lowest,
                highest))
            {
                Cleanup();
                return false;
            }
        }

        if (!gc_heap.initialize_region_bookkeeping() ||
            !gc_heap.allocate_initial_regions(heapCount) ||
            gc_heap.on_used_changed(
                gc_heap.global_region_allocator.get_left_used_unsafe()) == 0)
        {
            Cleanup();
            return false;
        }

        gc_heap.g_heaps = (gc_heap**)SyncImports.ManagedGC_AllocZeroed(
            unchecked((nuint)heapCount * (nuint)sizeof(gc_heap*)));
        if (gc_heap.g_heaps is null)
        {
            Cleanup();
            return false;
        }

        // Allocate the PER_HEAP_ISOLATED shared mark-list backing once for all heaps.
        if (!gc_heap.initialize_shared_mark_list(heapCount))
        {
            Cleanup();
            return false;
        }

        for (int i = 0; i < heapCount; i++)
        {
            gc_heap* heap = (gc_heap*)SyncImports.ManagedGC_AllocZeroed(
                (nuint)sizeof(gc_heap));
            if (heap is null)
            {
                Cleanup();
                return false;
            }

            gc_heap.g_heaps[i] = heap;
            gc_heap.initialize_server_allocation_state(heap, i);
            if (!heap->gc_done_event.CreateManualEventNoThrow(initialState: false))
            {
                Cleanup();
                return false;
            }

            generation* generationTable = gc_heap.generation_table_of(heap);
            if (!gc_heap.initial_make_soh_regions(
                    generationTable,
                    &heap->ephemeral_heap_segment,
                    &heap->alloc_allocated,
                    heap) ||
                !gc_heap.initial_make_uoh_regions(
                    (int)gc_generation_num.loh_generation,
                    generationTable,
                    heap) ||
                !gc_heap.initial_make_uoh_regions(
                    (int)gc_generation_num.poh_generation,
                    generationTable,
                    heap))
            {
                Cleanup();
                return false;
            }

            heap->server_finalize_queue = CFinalize.Allocate();
            if (heap->server_finalize_queue is null ||
                !gc_heap.initialize_mark_stack(heap) ||
                !gc_heap.create_gc_thread(heap))
            {
                Cleanup();
                return false;
            }
        }

        if (gc_heap.dynamic_adaptation_mode ==
            (int)gc_dynamic_adaptation_mode.dynamic_adaptation_to_application_sizes)
        {
            gc_heap.n_heaps = 1;
            gc_heap.dynamic_heap_count_data.last_n_heaps = 0;
            gc_heap.dynamic_heap_count_data.new_n_heaps = 1;
            gc_heap.dynamic_heap_count_data.init_only_p = 1;
            gc_heap.gc_t_join.update_n_threads(heapCount);
        }

        gc_heap.compute_gc_and_ephemeral_range(
            gc_heap.g_heaps[0],
            (int)gc_generation_num.soh_gen1,
            end_of_gc_p: true);
        GCWriteBarrier.stomp_write_barrier_initialize(
            gc_heap.ephemeral_low,
            gc_heap.ephemeral_high,
            gc_heap.map_region_to_generation_skewed,
            (byte)gc_heap.min_segment_size_shr);
        s_initialized = true;
        return true;
    }

    public static void Shutdown() => Cleanup();

    internal static gc_heap* Heap =>
        gc_heap.g_heaps is null || gc_heap.n_heaps == 0
            ? null
            : gc_heap.g_heaps[0];

    internal static gc_heap* HeapAt(int heapNumber) =>
        gc_heap.g_heaps is null ||
        (uint)heapNumber >= (uint)gc_heap.n_heaps
            ? null
            : gc_heap.g_heaps[heapNumber];

    internal static heap_segment* FindSegment(byte* address, bool smallHeapOnly)
    {
        if (!s_initialized ||
            address < GCCommon.g_gc_lowest_address ||
            address >= GCCommon.g_gc_highest_address)
        {
            return null;
        }

        if (!gc_heap.try_get_region_segment(
            address,
            smallHeapOnly,
            out heap_segment* segment))
        {
            return null;
        }
        return segment;
    }

    private static void Cleanup()
    {
        Volatile.Write(ref gc_heap.server_gc_shutdown, 1);
        if (gc_heap.gc_start_event.IsValid())
        {
            gc_heap.gc_start_event.Set();
        }

        int expectedThreads =
            Volatile.Read(ref gc_heap.server_gc_threads_created);
        while (Volatile.Read(ref gc_heap.server_gc_threads_exited) < expectedThreads)
        {
            GCToOSInterface.Sleep(1);
        }

        if (gc_heap.g_heaps is not null)
        {
            for (int i = 0; i < gc_heap.n_max_heaps; i++)
            {
                gc_heap* heap = gc_heap.g_heaps[i];
                if (heap is null)
                {
                    continue;
                }
                CFinalize.Free(heap->server_finalize_queue);
                gc_heap.free_server_mark_storage(heap);
                if (heap->gc_done_event.IsValid())
                {
                    heap->gc_done_event.CloseEvent();
                }
                SyncImports.ManagedGC_Free(heap);
            }
            SyncImports.ManagedGC_Free(gc_heap.g_heaps);
        }

        gc_heap.destroy_shared_mark_list();
        gc_heap.g_heaps = null;
        gc_heap.n_heaps = 0;
        gc_heap.n_max_heaps = 0;
        gc_heap.server_gc_shutdown = 0;
        gc_heap.server_gc_threads_created = 0;
        gc_heap.server_gc_threads_exited = 0;
        gc_heap.destroy_thread_support();
        heap_select.destroy();
        gc_heap.free_initial_regions();
        gc_heap.free_region_bookkeeping();
        gc_heap.global_region_allocator.destroy();
        if (s_reservedRegionRange is not null)
        {
            GCToOSInterface.VirtualRelease(
                s_reservedRegionRange,
                s_reservedRegionRangeSize);
        }
        s_reservedRegionRange = null;
        s_reservedRegionRangeSize = 0;
        s_initialized = false;
    }
}

internal static unsafe class ManagedGCHeap
{
    internal const uint MaxGeneration = 2;
    private const int S_OK = 0;
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const nuint LargeObjectSize = 85000;
    private const int MaxFrozenSegments = 64;

    private static IGCHeapInternalVtable s_vtable;
    private static nint s_vtablePtr;
    private static FrozenSegment* s_frozenSegments;
    private static int s_frozenSegmentCount;
    private static long s_totalAllocatedBytes;

    private struct FrozenSegment
    {
        public nint Start;
        public nint End;
        public nint Reserved;
    }

    public static void* Create()
    {
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
        s_vtable.IGCHeap.TemporaryEnableConcurrentGC = &TemporaryConcurrentGC;
        s_vtable.IGCHeap.TemporaryDisableConcurrentGC = &TemporaryConcurrentGC;
        s_vtable.IGCHeap.IsConcurrentGCEnabled = &IsConcurrentGCInProgress;
        s_vtable.IGCHeap.WaitUntilConcurrentGCCompleteAsync = &WaitUntilConcurrentGCCompleteAsync;
        s_vtable.IGCHeap.GetNumberOfFinalizable = &GetNumberOfFinalizable;
        s_vtable.IGCHeap.GetNextFinalizable = &GetNextFinalizable;
        s_vtable.IGCHeap.WhichGeneration = &WhichGeneration;
        s_vtable.IGCHeap.CollectionCount = &CollectionCount;
        s_vtable.IGCHeap.GetTotalBytesInUse = &GetTotalBytesInUse;
        s_vtable.IGCHeap.GetTotalAllocatedBytes = &GetTotalAllocatedBytes;
        s_vtable.IGCHeap.GetMemoryInfo = &GetMemoryInfo;
        s_vtable.IGCHeap.GetMaxGeneration = &GetMaxGeneration;
        s_vtable.IGCHeap.SetFinalizationRun = &SetFinalizationRun;
        s_vtable.IGCHeap.RegisterForFinalization = &RegisterForFinalization;
        s_vtable.IGCHeap.Initialize = &Initialize;
        s_vtable.IGCHeap.IsHeapPointer = &IsHeapPointer;
        s_vtable.IGCHeap.GetCondemnedGeneration = &GetCondemnedGeneration;
        s_vtable.IGCHeap.IsGCInProgressHelper = &IsGCInProgressHelper;
        s_vtable.IGCHeap.GetGcCount = &GetGcCount;
        s_vtable.IGCHeap.IsThreadUsingAllocationContextHeap = &IsThreadUsingAllocationContextHeap;
        s_vtable.IGCHeap.IsEphemeral = &IsEphemeral;
        s_vtable.IGCHeap.WaitUntilGCComplete = &WaitUntilGCComplete;
        s_vtable.IGCHeap.FixAllocContext = &FixAllocContext;
        s_vtable.IGCHeap.GetCurrentObjSize = &GetCurrentObjSize;
        s_vtable.IGCHeap.RuntimeStructuresValid = &RuntimeStructuresValid;
        s_vtable.IGCHeap.SetSuspensionPending = &SetSuspensionPending;
        s_vtable.IGCHeap.SetYieldProcessorScalingFactor = &SetYieldProcessorScalingFactor;
        s_vtable.IGCHeap.Shutdown = &Shutdown;
        s_vtable.IGCHeap.Alloc = &Alloc;
        s_vtable.IGCHeap.PublishObject = &PublishObject;
        s_vtable.IGCHeap.SetWaitForGCEvent = &SetWaitForGCEvent;
        s_vtable.IGCHeap.ResetWaitForGCEvent = &ResetWaitForGCEvent;
        s_vtable.IGCHeap.IsLargeObject = &IsLargeObject;
        s_vtable.IGCHeap.RegisterFrozenSegment = &RegisterFrozenSegment;
        s_vtable.IGCHeap.UnregisterFrozenSegment = &UnregisterFrozenSegment;
        s_vtable.IGCHeap.IsInFrozenSegment = &IsInFrozenSegment;
        s_vtable.IGCHeap.ControlEvents = &ControlEvents;
        s_vtable.IGCHeap.ControlPrivateEvents = &ControlPrivateEvents;
        s_vtable.IGCHeap.GetGenerationWithRange = &GetGenerationWithRange;
        s_vtable.IGCHeap.UpdateFrozenSegment = &UpdateFrozenSegment;
        s_vtable.IGCHeap.GetExtraWorkForFinalization =
            &GetExtraWorkForFinalization;
        s_vtable.IGCHeap.GetLOHThreshold = &GetLOHThreshold;
        s_vtable.GetNumberOfHeaps = &GetNumberOfHeaps;
        s_vtable.GetHomeHeapNumber = &GetHomeHeapNumber;
        s_vtable.GetPromotedBytes = &GetPromotedBytes;

        s_vtablePtr = (nint)Unsafe.AsPointer(ref s_vtable);
        return Unsafe.AsPointer(ref s_vtablePtr);
    }

    [RuntimeImport("*", "ManagedGC_Unsupported")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern void FailFastUnsupported();

    private static void Unsupported() => FailFastUnsupported();

    private static int Initialize(void* thisPtr)
    {
        _ = thisPtr;
        GCCommon.InitializeRuntimeLifecycleState();
        GCCommon.g_gc_pFreeObjectMethodTable = GCToEEInterface.GetFreeObjectMethodTable();
        GCCommon.g_num_processors = GCToOSInterface.GetTotalProcessorCount();
        Debug.Assert(GCCommon.g_num_processors != 0);
        if (!gc_heap.check_commit_cs.Initialize())
        {
            return E_OUTOFMEMORY;
        }
        gc_heap.initialize_gc_static_state();
        gc_heap.generation_skip_ratio_threshold = (int)GCConfig.GetGCLowSkipRatio();
        gc_heap.initialize_spin_count_unit();
        if (!ManagedGCRegionBootstrap.Initialize())
        {
            gc_heap.check_commit_cs.Destroy();
            return E_OUTOFMEMORY;
        }

        s_frozenSegments = (FrozenSegment*)SyncImports.ManagedGC_AllocZeroed(
            (nuint)(sizeof(FrozenSegment) * MaxFrozenSegments));
        if (s_frozenSegments is null)
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
            return E_OUTOFMEMORY;
        }

        return S_OK;
    }

    private static void Shutdown(void* thisPtr)
    {
        _ = thisPtr;
        if (s_frozenSegments is not null)
        {
            SyncImports.ManagedGC_Free(s_frozenSegments);
        }
        s_frozenSegments = null;
        s_frozenSegmentCount = 0;
        ManagedGCRegionBootstrap.Shutdown();
        gc_heap.check_commit_cs.Destroy();
    }

    private static byte* Alloc(
        void* thisPtr,
        gc_alloc_context* context,
        nuint size,
        uint flags)
    {
        _ = thisPtr;
        size = gc_heap.Align(size);
        gc_heap* heap = gc_heap.heap_of_context(context);
        bool uoh =
            (flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_USER_OLD_HEAP) != 0 ||
            size >= LargeObjectSize;
        int generationNumber = uoh
            ? ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_PINNED_OBJECT_HEAP) != 0
                ? (int)gc_generation_num.poh_generation
                : (int)gc_generation_num.loh_generation)
            : (int)gc_generation_num.soh_gen0;

        if (!uoh)
        {
            byte* current = context->alloc_ptr;
            if (current is not null &&
                size <= (nuint)(context->alloc_limit - current))
            {
                context->alloc_ptr = current + size;
                return current;
            }
        }

        GCSpinLock* allocationLock = generationNumber == 0
            ? &heap->more_space_lock_soh
            : &heap->more_space_lock_uoh;
        GCSpinLock.enter(allocationLock);
        generation* gen = gc_heap.generation_of(
            gc_heap.generation_table_of(heap),
            generationNumber);
        heap_segment* segment =
            generation.generation_allocation_segment(gen);
        byte* start = heap_segment.heap_segment_allocated(segment);
        byte* end = heap_segment.heap_segment_reserved(segment) -
            (nint)gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size);
        nuint quantum = uoh ? size : (size > heap->allocation_quantum ? size : heap->allocation_quantum);
        byte* allocationEnd = start + (nint)quantum;
        byte* commitStart = gc_heap.align_lower_page(start);
        byte* commitEnd = gc_heap.align_on_page(allocationEnd);
        if (quantum > (nuint)(end - start) ||
            (commitEnd > heap_segment.heap_segment_committed(segment) &&
             !GCToOSInterface.VirtualCommit(
                 commitStart,
                 (nuint)(commitEnd - commitStart))))
        {
            GCSpinLock.leave(allocationLock);
            return null;
        }

        byte* limit = allocationEnd;
        if (commitEnd > heap_segment.heap_segment_committed(segment))
        {
            heap_segment.heap_segment_committed(segment) = commitEnd;
        }
        heap_segment.heap_segment_allocated(segment) = limit;
        if (generationNumber == 0)
        {
            heap->alloc_allocated = limit;
            context->alloc_ptr = start + (nint)size;
            context->alloc_limit = limit;
            context->alloc_bytes = unchecked(context->alloc_bytes + (long)quantum);
            heap->total_alloc_bytes_soh += quantum;
        }
        else
        {
            context->alloc_bytes_uoh = unchecked(context->alloc_bytes_uoh + (long)size);
            heap->total_alloc_bytes_uoh += size;
        }
        GCSpinLock.leave(allocationLock);
        System.Threading.Interlocked.Add(
            ref s_totalAllocatedBytes,
            unchecked((long)(uoh ? size : quantum)));
        return start;
    }

    private static void FixAllocContext(
        void* thisPtr,
        gc_alloc_context* context,
        void* arg,
        void* heap)
    {
        _ = thisPtr;
        _ = arg;
        _ = heap;
        context->alloc_ptr = null;
        context->alloc_limit = null;
    }

    private static byte IsThreadUsingAllocationContextHeap(
        void* thisPtr,
        gc_alloc_context* context,
        int threadNumber)
    {
        _ = thisPtr;
        gc_heap* home = (gc_heap*)context->gc_reserved_2;
        return (home is null ? threadNumber == 0 : home->heap_number == threadNumber)
            ? (byte)1
            : (byte)0;
    }

    private static int GetNumberOfHeaps(void* thisPtr)
    {
        _ = thisPtr;
        return gc_heap.n_heaps;
    }

    private static int GetHomeHeapNumber(void* thisPtr)
    {
        _ = thisPtr;
        gc_alloc_context* context = GCToEEInterface.GetAllocContext();
        gc_heap* home = context is null ? null : (gc_heap*)context->gc_reserved_2;
        return home is null ? 0 : home->heap_number;
    }

    internal static int CurrentHomeHeapNumber => GetHomeHeapNumber(null);

    // GCScan.cs / HandleTableScan.cs are shared into the server build; these supply the server
    // forms of the WKS ManagedGCHeap members those files consume. Background collection is not
    // routed for the server configuration, so the promoted check follows the blocking-GC path
    // and no concurrent collection is ever in progress.
    internal static bool IsPromoted(byte* obj) =>
        !gc_heap.is_in_gc_range(obj) ||
        !gc_heap.is_in_condemned_gc(obj) ||
        ((CObjectHeader*)obj)->IsMarked() != 0;

    internal static nuint GetPromotedBytesForHandleScan(int heap_index)
    {
        gc_heap* hp = gc_heap.g_heaps is null || (uint)heap_index >= (uint)gc_heap.n_heaps
            ? null
            : gc_heap.g_heaps[heap_index];
        return hp is null ? 0 : gc_heap.get_promoted_bytes(hp);
    }

    internal static bool ConcurrentCollectionInProgress => false;

    // GCBridge.cs (FEATURE_JAVAMARSHAL, defined globally for NativeAOT) is shared into the server
    // build; these supply the server forms of the two ManagedGCHeap members it consumes.
    internal static bool IsPromotedForBridge(byte* obj) => IsPromoted(obj);

    private struct BridgeWalkContext
    {
        public delegate*<byte*, void*, byte> callback;
        public void* context;
    }

    internal static void DiagWalkObjectForBridge(
        byte* obj,
        delegate*<byte*, void*, byte> callback,
        void* context)
    {
        if (obj is null || gc_heap.contain_pointers(obj) == 0)
        {
            return;
        }

        BridgeWalkContext walkContext = new()
        {
            callback = callback,
            context = context,
        };
        gc_heap.go_through_object_nostart(
            gc_heap.method_table(obj),
            obj,
            gc_heap.size(obj),
            &walkContext,
            &BridgeWalkObjectReference);
    }

    private static void BridgeWalkObjectReference(byte** reference, void* context)
    {
        if (*reference is not null)
        {
            BridgeWalkContext* walkContext = (BridgeWalkContext*)context;
            walkContext->callback(*reference, walkContext->context);
        }
    }

    private static nuint GetPromotedBytes(void* thisPtr, int heapIndex)
    {
        _ = thisPtr;
        _ = heapIndex;
        return 0;
    }

    private static byte RegisterForFinalization(void* thisPtr, int gen, byte* obj)
    {
        _ = thisPtr;
        heap_segment* segment = ManagedGCRegionBootstrap.FindSegment(
            obj,
            smallHeapOnly: false);
        gc_heap* heap = segment is null ? null : segment->heap;
        if (heap is null)
        {
            heap = ManagedGCRegionBootstrap.Heap;
        }
        return heap is not null &&
            heap->server_finalize_queue->RegisterForFinalization(gen, obj)
            ? (byte)1
            : (byte)0;
    }

    private static nuint GetNumberOfFinalizable(void* thisPtr)
    {
        _ = thisPtr;
        nuint count = 0;
        for (int i = 0; i < gc_heap.n_heaps; i++)
        {
            count += gc_heap.g_heaps[i]->server_finalize_queue->GetNumberFinalizableObjects();
        }
        return count;
    }

    private static byte* GetNextFinalizable(void* thisPtr)
    {
        _ = thisPtr;
        for (int i = 0; i < gc_heap.n_heaps; i++)
        {
            byte* obj = gc_heap.g_heaps[i]->server_finalize_queue->GetNextFinalizableObject();
            if (obj is not null)
            {
                return obj;
            }
        }
        return null;
    }

    private static FinalizerWorkItem* GetExtraWorkForFinalization(
        void* thisPtr)
    {
        _ = thisPtr;
        return null;
    }

    private static void SetFinalizationRun(void* thisPtr, byte* obj)
    {
        _ = thisPtr;
        ((CObjectHeader*)obj)->GetHeader()->SetFinalizerRun();
    }

    private static heap_segment* FindSegment(byte* obj) =>
        ManagedGCRegionBootstrap.FindSegment(obj, smallHeapOnly: false);

    private static byte IsHeapPointer(void* thisPtr, void* obj, byte smallHeapOnly)
    {
        _ = thisPtr;
        return ManagedGCRegionBootstrap.FindSegment(
            (byte*)obj,
            smallHeapOnly != 0) is not null ? (byte)1 : (byte)0;
    }

    private static byte IsEphemeral(void* thisPtr, byte* obj)
    {
        _ = thisPtr;
        heap_segment* segment = FindSegment(obj);
        return segment is not null &&
            segment->gen_num < GCInterfaceOffsets.max_generation
            ? (byte)1
            : (byte)0;
    }

    internal static uint GenerationOf(byte* obj)
    {
        heap_segment* segment = FindSegment(obj);
        if (segment is null)
        {
            return uint.MaxValue;
        }
        if (heap_segment.heap_segment_loh_p(segment) != 0)
        {
            return (uint)gc_generation_num.loh_generation;
        }
        if (heap_segment.heap_segment_poh_p(segment) != 0)
        {
            return (uint)gc_generation_num.poh_generation;
        }
        return segment->gen_num;
    }

    private static uint WhichGeneration(void* thisPtr, byte* obj)
    {
        _ = thisPtr;
        return GenerationOf(obj);
    }

    private static uint GetGenerationWithRange(
        void* thisPtr,
        byte* obj,
        byte** start,
        byte** allocated,
        byte** reserved)
    {
        _ = thisPtr;
        heap_segment* segment = FindSegment(obj);
        if (segment is null)
        {
            *start = null;
            *allocated = null;
            *reserved = null;
            return uint.MaxValue;
        }
        *start = heap_segment.heap_segment_mem(segment);
        *allocated = heap_segment.heap_segment_allocated(segment);
        *reserved = heap_segment.heap_segment_reserved(segment);
        return WhichGeneration(null, obj);
    }

    private static segment_handle RegisterFrozenSegment(void* thisPtr, segment_info* info)
    {
        _ = thisPtr;
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
                segment->End = (nint)((byte*)info->pvMem + info->ibAllocated);
                segment->Reserved = (nint)((byte*)info->pvMem + info->ibReserved);
                Volatile.Write(
                    ref segment->Start,
                    (nint)((byte*)info->pvMem + info->ibFirstObject));
                return new segment_handle(segment);
            }
        }
    }

    private static void UpdateFrozenSegment(
        void* thisPtr,
        segment_handle segment,
        byte* allocated,
        byte* committed)
    {
        _ = thisPtr;
        _ = committed;
        Volatile.Write(ref ((FrozenSegment*)segment.Value)->End, (nint)allocated);
    }

    private static void UnregisterFrozenSegment(void* thisPtr, segment_handle segment)
    {
        _ = thisPtr;
        FrozenSegment* frozen = (FrozenSegment*)segment.Value;
        Volatile.Write(ref frozen->Start, 0);
        frozen->End = 0;
        frozen->Reserved = 0;
    }

    private static byte IsInFrozenSegment(void* thisPtr, byte* obj)
    {
        _ = thisPtr;
        int count = Volatile.Read(ref s_frozenSegmentCount);
        for (int i = 0; i < count; i++)
        {
            FrozenSegment* segment = s_frozenSegments + i;
            nint start = Volatile.Read(ref segment->Start);
            nint end = Volatile.Read(ref segment->End);
            if (start != 0 && (nint)obj >= start && (nint)obj < end)
            {
                return 1;
            }
        }
        return 0;
    }

    private static nuint GetTotalBytesInUse(void* thisPtr)
    {
        _ = thisPtr;
        nuint total = 0;
        for (int i = 0; i < gc_heap.n_heaps; i++)
        {
            total += unchecked((nuint)gc_heap.g_heaps[i]->total_alloc_bytes_soh);
            total += unchecked((nuint)gc_heap.g_heaps[i]->total_alloc_bytes_uoh);
        }
        return total;
    }

    private static ulong GetTotalAllocatedBytes(void* thisPtr) =>
        unchecked((ulong)System.Threading.Interlocked.Read(
            ref s_totalAllocatedBytes));

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
        _ = thisPtr;
        _ = kind;
        *highMemLoadThresholdBytes = 0;
        *totalAvailableMemoryBytes = 0;
        *lastRecordedMemLoadBytes = 0;
        *lastRecordedHeapSizeBytes = GetTotalBytesInUse(null);
        *lastRecordedFragmentationBytes = 0;
        *totalCommittedBytes = *lastRecordedHeapSizeBytes;
        *promotedBytes = 0;
        *pinnedObjectCount = 0;
        *finalizationPendingCount = 0;
        *index = 0;
        *generation = 0;
        *pauseTimePct = 0;
        *isCompaction = 0;
        *isConcurrent = 0;
        for (int i = 0; i < 4 * (int)gc_generation_num.total_generation_count; i++)
        {
            genInfoRaw[i] = 0;
        }

        for (int i = 0; i < 2; i++)
        {
            pauseInfoRaw[i] = 0;
        }
    }

    private static nuint GetCurrentObjSize(void* thisPtr) =>
        GetTotalBytesInUse(thisPtr);

    private static byte IsValidSegmentSize(void* thisPtr, nuint size) => 1;
    private static byte IsValidGen0MaxSize(void* thisPtr, nuint size) => 1;
    private static nuint GetValidSegmentSize(void* thisPtr, byte largeSegment) =>
        largeSegment != 0
            ? gc_heap.global_region_allocator.get_large_region_alignment()
            : gc_heap.global_region_allocator.get_region_alignment();
    private static void SetReservedVMLimit(void* thisPtr, nuint limit) =>
        gc_heap.reserved_memory_limit = limit;
    private static uint GetMaxGeneration(void* thisPtr) => MaxGeneration;
    private static uint GetCondemnedGeneration(void* thisPtr) => uint.MaxValue;
    private static int CollectionCount(void* thisPtr, int generation, int kind) => 0;
    private static uint GetGcCount(void* thisPtr) => 0;
    private static byte IsGCInProgressHelper(void* thisPtr, byte considerStart) => 0;
    private static uint WaitUntilGCComplete(void* thisPtr, byte considerStart) => 0;
    private static void WaitUntilConcurrentGCComplete(void* thisPtr) { }
    private static int WaitUntilConcurrentGCCompleteAsync(void* thisPtr, int timeout) => S_OK;
    private static byte IsConcurrentGCInProgress(void* thisPtr) => 0;
    private static void TemporaryConcurrentGC(void* thisPtr) { }
    private static byte RuntimeStructuresValid(void* thisPtr) => 1;
    private static void PublishObject(void* thisPtr, byte* obj) { }
    private static byte IsLargeObject(void* thisPtr, byte* obj) =>
        WhichGeneration(thisPtr, obj) == (uint)gc_generation_num.loh_generation
            ? (byte)1
            : (byte)0;
    private static nuint GetLOHThreshold(void* thisPtr) => LargeObjectSize;
    private static void SetWaitForGCEvent(void* thisPtr) => GCCommon.SetWaitForGCEvent();
    private static void ResetWaitForGCEvent(void* thisPtr) => GCCommon.ResetWaitForGCEvent();
    private static void SetSuspensionPending(void* thisPtr, byte pending) =>
        GCCommon.SetSuspensionPending(pending != 0);
    private static void SetYieldProcessorScalingFactor(void* thisPtr, float factor) =>
        gc_heap.set_yield_processor_scaling_factor(factor);
    private static void ControlEvents(void* thisPtr, GCEventKeyword keyword, GCEventLevel level) =>
        GCEventStatus.Set(GCEventProvider.Default, keyword, level);
    private static void ControlPrivateEvents(void* thisPtr, GCEventKeyword keyword, GCEventLevel level) =>
        GCEventStatus.Set(GCEventProvider.Private, keyword, level);
}

#pragma warning restore CS8981

#endif
