// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class GCRecordTests
{
    [Fact]
    public void CondemnReasonsUseNativeBitPacking()
    {
        gen_to_condemn_tuning tuning = default;
        tuning.init();

        tuning.set_gen(gc_condemn_reason_gen.gen_initial, 2);
        tuning.set_gen(gc_condemn_reason_gen.gen_initial, 1);
        tuning.set_gen(gc_condemn_reason_gen.gen_alloc_budget, 1);
        tuning.set_condition(gc_condemn_reason_condition.gen_high_mem_p);
        tuning.set_condition(gc_condemn_reason_condition.gen_before_bgc);

        Assert.Equal(3u, tuning.get_gen(gc_condemn_reason_gen.gen_initial));
        Assert.Equal(1u, tuning.get_gen(gc_condemn_reason_gen.gen_alloc_budget));
        Assert.Equal(0x13u, tuning.get_reasons0());
        Assert.Equal(1u << 2, tuning.get_condition(gc_condemn_reason_condition.gen_high_mem_p));
        Assert.Equal((1u << 2) | (1u << 15), tuning.get_reasons1());
        Assert.False(tuning.is_only_condition(gc_condemn_reason_condition.gen_high_mem_p));

        gen_to_condemn_tuning copy = default;
        copy.init(&tuning);
        Assert.Equal(tuning.get_reasons0(), copy.get_reasons0());
        Assert.Equal(tuning.get_reasons1(), copy.get_reasons1());
    }

    [Fact]
    public void SingleConditionIsDetected()
    {
        gen_to_condemn_tuning tuning = default;
        tuning.set_condition(gc_condemn_reason_condition.gen_joined_aggressive);

        Assert.True(tuning.is_only_condition(gc_condemn_reason_condition.gen_joined_aggressive));
    }

    [Fact]
    public void RecordStructsMatchNativePointerSizedLayout()
    {
        Assert.Equal(8, sizeof(gen_to_condemn_tuning));
        Assert.Equal(IntPtr.Size * 10, sizeof(gc_generation_data));
        Assert.Equal(IntPtr.Size == 8 ? 56 : 28, sizeof(maxgen_size_increase));
        Assert.Equal(IntPtr.Size == 8 ? 32 : 16, sizeof(fgm_history));

        Assert.Equal(0, Marshal.OffsetOf<gc_generation_data>(nameof(gc_generation_data.size_before)).ToInt32());
        Assert.Equal(
            IntPtr.Size * 9,
            Marshal.OffsetOf<gc_generation_data>(nameof(gc_generation_data.new_allocation)).ToInt32());
        Assert.Equal(
            IntPtr.Size * 6,
            Marshal.OffsetOf<maxgen_size_increase>(nameof(maxgen_size_increase.running_free_list_efficiency)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<fgm_history>(nameof(fgm_history.fgm)).ToInt32());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf<fgm_history>(nameof(fgm_history.size)).ToInt32());
        Assert.Equal(IntPtr.Size * 2, Marshal.OffsetOf<fgm_history>(nameof(fgm_history.available_pagefile_mb)).ToInt32());
        Assert.Equal(IntPtr.Size * 3, Marshal.OffsetOf<fgm_history>(nameof(fgm_history.loh_p)).ToInt32());
    }

    [Fact]
    public void PerHeapMechanismsUseNativeEncoding()
    {
        gc_history_per_heap history = default;

        history.set_mechanism(
            gc_mechanism_per_heap.gc_heap_expand,
            (uint)gc_heap_expand_mechanism.expand_new_seg);
        history.set_mechanism(
            gc_mechanism_per_heap.gc_heap_compact,
            (uint)gc_heap_compact_reason.compact_high_mem_frag);

        Assert.Equal(
            (int)gc_heap_expand_mechanism.expand_new_seg,
            history.get_mechanism(gc_mechanism_per_heap.gc_heap_expand));
        Assert.Equal(
            (int)gc_heap_compact_reason.compact_high_mem_frag,
            history.get_mechanism(gc_mechanism_per_heap.gc_heap_compact));

        history.clear_mechanism(gc_mechanism_per_heap.gc_heap_expand);
        Assert.Equal(-1, history.get_mechanism(gc_mechanism_per_heap.gc_heap_expand));
    }

    [Fact]
    public void PerHeapMechanismBitsCanBeSetAndCleared()
    {
        gc_history_per_heap history = default;

        history.set_mechanism_bit(gc_mechanism_bit_per_heap.gc_mark_list_bit);
        history.set_mechanism_bit(gc_mechanism_bit_per_heap.gc_demotion_bit);

        Assert.True(history.is_mechanism_bit_set(gc_mechanism_bit_per_heap.gc_mark_list_bit));
        Assert.True(history.is_mechanism_bit_set(gc_mechanism_bit_per_heap.gc_demotion_bit));

        history.clear_mechanism_bit(gc_mechanism_bit_per_heap.gc_mark_list_bit);
        Assert.False(history.is_mechanism_bit_set(gc_mechanism_bit_per_heap.gc_mark_list_bit));
        Assert.True(history.is_mechanism_bit_set(gc_mechanism_bit_per_heap.gc_demotion_bit));
    }

    [Fact]
    public void GlobalMechanismBitsUseNativeEncoding()
    {
        gc_history_global history = default;

        history.set_mechanism_p(gc_global_mechanism_p.global_concurrent);
        history.set_mechanism_p(gc_global_mechanism_p.global_card_bundles);

        Assert.True(history.get_mechanism_p(gc_global_mechanism_p.global_concurrent));
        Assert.True(history.get_mechanism_p(gc_global_mechanism_p.global_card_bundles));
        Assert.False(history.get_mechanism_p(gc_global_mechanism_p.global_compaction));
        Assert.Equal(0x11u, history.global_mechanisms_p);
    }
}
