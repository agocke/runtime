// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "unixasmmacros.inc"
#include "asmconstants.h"

// Note that the offsets specified in pcaddi must match the behavior of GetStubCodePageSize() on this architecture/os.

LEAF_ENTRY StubPrecodeCode
    pcaddi  $r21, 0x1000
    ld.d  $t2,$r21, StubPrecodeData__SecretParam
    ld.d  $r21,$r21, StubPrecodeData__Target
    jirl  $r0,$r21,0
LEAF_END_MARKED StubPrecodeCode

LEAF_ENTRY FixupPrecodeCode
    pcaddi  $r21, 0x1000
    ld.d  $r21,$r21, FixupPrecodeData__Target
    jirl  $r0,$r21,0
    pcaddi  $r21, 0xFFD
    dbar 0
    ld.d  $t2,$r21,  FixupPrecodeData__MethodDesc
    ld.d  $r21,$r21, FixupPrecodeData__PrecodeFixupThunk
    jirl  $r0,$r21,0
LEAF_END_MARKED FixupPrecodeCode

// NOTE: For LoongArch64 `CallCountingStubData__RemainingCallCountCell` must be zero !!!
// Because the stub-identifying token is $t1 within the `OnCallCountThresholdReachedStub`.
LEAF_ENTRY CallCountingStubCode
    pcaddi  $t2, 0x1000
    ld.d  $t1, $t2, CallCountingStubData__RemainingCallCountCell
    ld.h  $r21, $t1, 0
    addi.w  $r21, $r21, -1
    st.h  $r21, $t1, 0
    beq   $r21, $zero, LOCAL_LABEL(CountReachedZero)
    ld.d  $r21, $t2, CallCountingStubData__TargetForMethod
    jirl  $r0,$r21,0
LOCAL_LABEL(CountReachedZero):
    ld.d  $r21, $t2, CallCountingStubData__TargetForThresholdReached
    jirl  $r0,$r21,0
LEAF_END_MARKED CallCountingStubCode
