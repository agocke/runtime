// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Validates that the layouts recorded in GCInterfaceOffsets.h - which the C# port of the GC in
// System.Private.GC is compiled against - still match the C++ definitions of the GC/EE interface
// types. If this file fails to compile, either the C++ interface changed and the table plus the
// managed structs need to be updated, or the table is simply wrong.

#include "common.h"
#include "gcenv.h"
#include "gcheaputilities.h"
#include "handletablepriv.h"

class GCInterfaceOffsets
{
#define TO_STRING(x) #x
#define OFFSET_STRING(cls, member) TO_STRING(offsetof(cls, member))

#define PLAT_GC_OFFSET(offset, cls, member) \
    static_assert(offsetof(cls, member) == 0x##offset, "Bad GC interface offset for '" #cls "." #member "'. Actual offset: " OFFSET_STRING(cls, member));

// static_assert cannot report the actual size, so unlike PLAT_GC_OFFSET there is nothing useful
// to append to the message here.
#define PLAT_GC_SIZEOF(size, cls) \
    static_assert(sizeof(cls) == 0x##size, "Bad GC interface size for '" #cls "'.");

// alignof is what determines the trailing padding a type carries and how an array of it is
// laid out, neither of which the offsets above can express on their own.
#define PLAT_GC_ALIGNOF(align, cls) \
    static_assert(alignof(cls) == 0x##align, "Bad GC interface alignment for '" #cls "'.");

#define PLAT_GC_CONST(constant, expr) \
    static_assert((expr) == 0x##constant, "Bad GC interface constant for '" #expr "'.");

// For a C++ expression that is not spelled the same way in C#, such as a scoped enumerator.
#define PLAT_GC_VALUE(constant, name, expr) \
    static_assert((int)(expr) == 0x##constant, "Bad GC interface constant for '" #expr "'.");

#include "../System.Private.GC/GCInterfaceOffsets.h"

};

#ifdef _MSC_VER
namespace { char WorkaroundLNK4221Warning; };
#endif
