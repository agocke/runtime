// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the WKS full-GC notification paths in allocation.cpp, collect.cpp, and interface.cpp.

using System;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
#if USE_REGIONS && !MULTIPLE_HEAPS
    private const nuint fgn_check_quantum = 2 * 1024 * 1024;

    public static bool initialize_full_gc_notification()
    {
        if (!full_gc_approach_event.CreateManualEventNoThrow(initialState: false))
        {
            return false;
        }

        if (!full_gc_end_event.CreateManualEventNoThrow(initialState: false))
        {
            full_gc_approach_event.CloseEvent();
            return false;
        }

        fgn_loh_percent = 0;
        Volatile.Write(ref full_gc_approach_event_set, false);
#if BACKGROUND_GC
        fgn_last_gc_was_concurrent = 0;
#endif
        return true;
    }

    public static void destroy_full_gc_notification()
    {
        if (full_gc_approach_event.IsValid())
        {
            full_gc_approach_event.CloseEvent();
        }

        if (full_gc_end_event.IsValid())
        {
            full_gc_end_event.CloseEvent();
        }
    }

    public static bool register_for_full_gc_notification(
        gc_heap* hp,
        uint gen2_percentage,
        uint loh_percentage)
    {
        fgn_last_alloc = unchecked((nuint)dynamic_data.dd_new_allocation(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)));
        fgn_maxgen_percent = gen2_percentage;

        full_gc_approach_event.Reset();
        full_gc_end_event.Reset();
        Volatile.Write(ref full_gc_approach_event_set, false);
        fgn_loh_percent = loh_percentage;
        return true;
    }

    public static bool cancel_full_gc_notification()
    {
        fgn_maxgen_percent = 0;
        fgn_loh_percent = 0;
        full_gc_approach_event.Set();
        full_gc_end_event.Set();
        return true;
    }

    public static wait_full_gc_status full_gc_wait(
        GCEvent* @event,
        int time_out_ms)
    {
        if (fgn_maxgen_percent == 0)
        {
            return wait_full_gc_status.wait_full_gc_na;
        }

#if MANAGED_GC_TEST_HOST
        uint wait_result = @event->Wait(
            unchecked((uint)time_out_ms),
            alertable: false);
#else
        uint wait_result = @event->UserThreadWait(
            unchecked((uint)time_out_ms));
#endif

        if (wait_result == GCEnv.WAIT_OBJECT_0 ||
            wait_result == GCEnv.WAIT_TIMEOUT)
        {
            if (fgn_maxgen_percent == 0)
            {
                return wait_full_gc_status.wait_full_gc_cancelled;
            }

            if (wait_result == GCEnv.WAIT_OBJECT_0)
            {
#if BACKGROUND_GC
                if (fgn_last_gc_was_concurrent != 0)
                {
                    fgn_last_gc_was_concurrent = 0;
                    return wait_full_gc_status.wait_full_gc_na;
                }
#endif
                return wait_full_gc_status.wait_full_gc_success;
            }

            return wait_full_gc_status.wait_full_gc_timeout;
        }

        return wait_full_gc_status.wait_full_gc_failed;
    }

    public static void check_for_full_gc(gc_heap* hp, int gen_num, nuint size)
    {
        bool should_notify = false;
        bool alloc_factor = true;
        int n_initial = gen_num;
        bool local_blocking_collection = false;
        int new_alloc_remain_percent = 0;

        if (Volatile.Read(ref full_gc_approach_event_set))
        {
            return;
        }

        if (gen_num < GCInterfaceOffsets.max_generation)
        {
            gen_num = GCInterfaceOffsets.max_generation;
        }

        dynamic_data* dd_full = dynamic_data_of(hp, gen_num);
        nint new_alloc_remain;
        uint pct = n_initial >= (int)gc_generation_num.loh_generation
            ? fgn_loh_percent
            : fgn_maxgen_percent;

        if (n_initial == 0)
        {
            dynamic_data* dd_0 = dynamic_data_of(
                hp,
                (int)gc_generation_num.soh_gen0);
            nint new_allocation_0 = dynamic_data.dd_new_allocation(dd_0);
            if (unchecked(
                    fgn_last_alloc - (nuint)new_allocation_0) <
                    fgn_check_quantum &&
                new_allocation_0 >= 0)
            {
                return;
            }

            fgn_last_alloc = unchecked((nuint)new_allocation_0);
            size = 0;
        }

        int n = 0;
        for (int i = 1; i <= GCInterfaceOffsets.max_generation; i++)
        {
            if (dynamic_data.dd_new_allocation(dynamic_data_of(hp, i)) <= 0)
            {
                n = i;
            }
            else
            {
                break;
            }
        }

        if (gen_num == GCInterfaceOffsets.max_generation &&
            n < GCInterfaceOffsets.max_generation - 1)
        {
            goto check_other_factors;
        }

        new_alloc_remain =
            dynamic_data.dd_new_allocation(dd_full) - (nint)size;
        nint desired_allocation =
            (nint)dynamic_data.dd_desired_allocation(dd_full);
        if (desired_allocation != 0)
        {
            new_alloc_remain_percent = (int)(
                (float)new_alloc_remain / desired_allocation * 100);
        }

        if (new_alloc_remain_percent <= (int)pct)
        {
#if BACKGROUND_GC
            if (background_allowed_p())
            {
                goto check_other_factors;
            }
#endif
            should_notify = true;
            goto done;
        }

    check_other_factors:
        n = generation_to_condemn_for_full_gc_notification(n);

#if BACKGROUND_GC
        if (n == GCInterfaceOffsets.max_generation && background_running_p())
        {
            n = GCInterfaceOffsets.max_generation - 1;
        }

        if (n == GCInterfaceOffsets.max_generation)
        {
            local_blocking_collection = !background_allowed_p();
        }
#else
        local_blocking_collection = true;
#endif

        if (n == GCInterfaceOffsets.max_generation && local_blocking_collection)
        {
            alloc_factor = false;
            should_notify = true;
        }

    done:
        if (should_notify)
        {
            send_full_gc_notification(n_initial, alloc_factor);
        }
    }

    public static void send_full_gc_notification(
        int gen_num,
        bool due_to_alloc_p)
    {
        if (!Volatile.Read(ref full_gc_approach_event_set))
        {
            GCEvents.GCEventFireGCFullNotify_V1(
                unchecked((uint)gen_num),
                due_to_alloc_p ? 1u : 0u);
            full_gc_end_event.Reset();
            full_gc_approach_event.Set();
            Volatile.Write(ref full_gc_approach_event_set, true);
        }
    }

    public static void update_full_gc_notification_after_gc(gc_heap* hp)
    {
        if (fgn_maxgen_percent == 0)
        {
            return;
        }

        if (settings.condemned_generation ==
            GCInterfaceOffsets.max_generation - 1)
        {
            check_for_full_gc(
                hp,
                GCInterfaceOffsets.max_generation - 1,
                0);
        }
        else if (settings.condemned_generation ==
                 GCInterfaceOffsets.max_generation &&
                 Volatile.Read(ref full_gc_approach_event_set))
        {
            full_gc_approach_event.Reset();
#if BACKGROUND_GC
            fgn_last_gc_was_concurrent = settings.concurrent != 0 ? 1 : 0;
#endif
            full_gc_end_event.Set();
            Volatile.Write(ref full_gc_approach_event_set, false);
        }
    }

    private static int generation_to_condemn_for_full_gc_notification(
        int initial_generation)
    {
        int condemned_generation = Math.Clamp(
            initial_generation,
            0,
            GCInterfaceOffsets.max_generation);

        if (last_gc_before_oom != 0)
        {
            condemned_generation = GCInterfaceOffsets.max_generation;
        }

        return condemned_generation;
    }

#if BACKGROUND_GC
    public static bool background_allowed_p() =>
        concurrent_gc_enabled() &&
        settings.pause_mode is
            gc_pause_mode.pause_interactive or
            gc_pause_mode.pause_sustained_low_latency;
#endif
#endif
}
#pragma warning restore CS8981
