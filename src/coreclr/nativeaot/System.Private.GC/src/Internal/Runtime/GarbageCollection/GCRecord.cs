// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the dependency-free prefix of src/coreclr/gc/gcrecord.h.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    internal enum gc_condemn_reason_gen
    {
        gen_initial = 0,
        gen_final_per_heap = 1,
        gen_alloc_budget = 2,
        gen_time_tuning = 3,
        gcrg_max = 4,
    }

    internal enum gc_condemn_reason_condition
    {
        gen_induced_fullgc_p = 0,
        gen_expand_fullgc_p = 1,
        gen_high_mem_p = 2,
        gen_very_high_mem_p = 3,
        gen_low_ephemeral_p = 4,
        gen_low_card_p = 5,
        gen_eph_high_frag_p = 6,
        gen_max_high_frag_p = 7,
        gen_max_high_frag_e_p = 8,
        gen_max_high_frag_m_p = 9,
        gen_max_high_frag_vm_p = 10,
        gen_max_gen1 = 11,
        gen_before_oom = 12,
        gen_gen2_too_small = 13,
        gen_induced_noforce_p = 14,
        gen_before_bgc = 15,
        gen_almost_max_alloc = 16,
        gen_joined_avoid_unproductive = 17,
        gen_joined_pm_induced_fullgc_p = 18,
        gen_joined_pm_alloc_loh = 19,
        gen_joined_gen1_in_pm = 20,
        gen_joined_limit_before_oom = 21,
        gen_joined_limit_loh_frag = 22,
        gen_joined_limit_loh_reclaim = 23,
        gen_joined_servo_initial = 24,
        gen_joined_servo_ngc = 25,
        gen_joined_servo_bgc = 26,
        gen_joined_servo_postpone = 27,
        gen_joined_stress_mix = 28,
        gen_joined_stress = 29,
        gen_joined_aggressive = 30,
        gcrc_max = 31,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct gen_to_condemn_tuning
    {
        public const int bits_generation = 2;
        public const uint generation_mask = 3;

        private uint condemn_reasons_gen;
        private uint condemn_reasons_condition;

        public void init()
        {
            condemn_reasons_gen = 0;
            condemn_reasons_condition = 0;
        }

        public void init(gen_to_condemn_tuning* reasons)
        {
            condemn_reasons_gen = reasons->condemn_reasons_gen;
            condemn_reasons_condition = reasons->condemn_reasons_condition;
        }

        public void set_gen(gc_condemn_reason_gen condemn_gen_reason, uint value)
        {
            Debug.Assert((value & ~generation_mask) == 0);
            condemn_reasons_gen |= value << ((int)condemn_gen_reason * bits_generation);
        }

        public void set_condition(gc_condemn_reason_condition condemn_gen_reason)
        {
            condemn_reasons_condition |= 1u << (int)condemn_gen_reason;
        }

        public bool is_only_condition(gc_condemn_reason_condition condition_to_check)
        {
            uint temp_conditions = 1u << (int)condition_to_check;
            return (condemn_reasons_condition ^ temp_conditions) == 0;
        }

        public uint get_gen(gc_condemn_reason_gen condemn_gen_reason)
        {
            return (condemn_reasons_gen >> ((int)condemn_gen_reason * bits_generation))
                & generation_mask;
        }

        public uint get_condition(gc_condemn_reason_condition condemn_gen_reason)
        {
            return condemn_reasons_condition & (1u << (int)condemn_gen_reason);
        }

        public uint get_reasons0()
        {
            return condemn_reasons_gen;
        }

        public uint get_reasons1()
        {
            return condemn_reasons_condition;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct gc_generation_data
    {
        public nuint size_before;
        public nuint free_list_space_before;
        public nuint free_obj_space_before;
        public nuint size_after;
        public nuint free_list_space_after;
        public nuint free_obj_space_after;
        public nuint @in;
        public nuint pinned_surv;
        public nuint npinned_surv;
        public nuint new_allocation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct maxgen_size_increase
    {
        public nuint free_list_allocated;
        public nuint free_list_rejected;
        public nuint end_seg_allocated;
        public nuint condemned_allocated;
        public nuint pinned_allocated;
        public nuint pinned_allocated_advance;
        public uint running_free_list_efficiency;
    }
}
