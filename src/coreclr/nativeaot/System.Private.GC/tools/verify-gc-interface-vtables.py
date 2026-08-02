#!/usr/bin/env python3

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Verifies the managed GC/EE interface vtables against the native GC/EE interfaces.

C++ does not offer a portable `offsetof` equivalent for virtual slots, so the correspondence
between the abstract classes in gcinterface.h / gcinterface.ee.h / gc.h and the function-pointer
structs in GCInterfaceVtables.cs cannot be checked by a static_assert the way the shared
structure layouts in GCInterfaceOffsets.h are. This script checks it instead, comparing, for
every interface:

  * the number of virtual slots,
  * the declaration order and name of every slot,
  * the full signature of every slot, after mapping C++ types to the C# types the port uses,
  * the calling convention the slot is declared with, which differs by call direction.

Interfaces that the managed GC implements are called by the native EE, and their slots hold the
address of a static C# method, so they are *managed* function pointers. Interfaces that the
native EE implements are called by the managed GC without changing GC mode, so their slots are
`delegate* unmanaged[SuppressGCTransition]`.
"""

import argparse
from pathlib import Path
import re
import sys


# (interface, header, base interface or None, whether the managed GC implements it)
INTERFACES = (
    ("IGCHandleStore", "gcinterface", None, True),
    ("IGCHandleManager", "gcinterface", None, True),
    ("IGCHeap", "gcinterface", None, True),
    ("IGCHeapInternal", "gc", "IGCHeap", True),
    ("IGCToCLR", "gcinterface_ee", None, False),
    ("IGCToCLREventSink", "gcinterface_ee", None, False),
)

# The calling convention a slot is declared with, by call direction.
IMPLEMENTED_BY_GC_CONVENTION = "delegate*"
IMPLEMENTED_BY_EE_CONVENTION = "delegate*unmanaged[SuppressGCTransition]"

# Callbacks that cross the boundary as arguments are always native function pointers, whichever
# side declares the slot that carries them.
CALLBACK_CONVENTION = "delegate*unmanaged"

# How the port spells each C++ type that appears in a GC/EE interface signature. Types that are
# opaque to the GC (Thread, MethodTable, the interfaces themselves) become void*, object
# references become byte* because the GC never holds a managed reference, and C++ bool becomes
# byte because bool is not blittable in a function-pointer signature.
TYPES = {
    "void": "void",
    "bool": "byte",
    "float": "float",
    "int": "int",
    "unsigned": "uint",
    "unsigned int": "uint",
    "int8_t": "sbyte",
    "uint8_t": "byte",
    "int16_t": "short",
    "uint16_t": "ushort",
    "int32_t": "int",
    "uint32_t": "uint",
    "int64_t": "long",
    "uint64_t": "ulong",
    "size_t": "nuint",
    "uintptr_t": "nuint",
    "ptrdiff_t": "nint",
    "char": "byte",
    "HRESULT": "int",
    "BOOL": "int",
    "Object": "byte",
    "_UNCHECKED_OBJECTREF": "byte",
    "PTR_PTR_Object": "byte**",
    "PTR_UNCHECKED_OBJECTREF": "byte**",
    "Thread": "void",
    "MethodTable": "void",
    "StressLogMsg": "void",
    "IGCHandleStore": "void",
    "IGCToCLREventSink": "void",
    # Types the port translates one-for-one keep their name.
    "OBJECTHANDLE": "OBJECTHANDLE",
    "HandleType": "HandleType",
    "segment_handle": "segment_handle",
    "segment_info": "segment_info",
    "gc_alloc_context": "gc_alloc_context",
    "ScanContext": "ScanContext",
    "WriteBarrierParameters": "WriteBarrierParameters",
    "EtwGCSettingsInfo": "EtwGCSettingsInfo",
    "MarkCrossReferencesArgs": "MarkCrossReferencesArgs",
    "FinalizerWorkItem": "FinalizerWorkItem",
    "NoGCRegionCallbackFinalizerWorkItem": "NoGCRegionCallbackFinalizerWorkItem",
    "GCEventKeyword": "GCEventKeyword",
    "GCEventLevel": "GCEventLevel",
    "GCConfigurationType": "GCConfigurationType",
    "SUSPEND_REASON": "SUSPEND_REASON",
    "walk_surv_type": "walk_surv_type",
    "enable_no_gc_region_callback_status": "enable_no_gc_region_callback_status",
}

# Qualifiers and calling-convention macros that do not affect the translated signature.
NOISE = re.compile(r"\b(?:const|volatile|struct|class|enum|CALLBACK|__stdcall|__cdecl|LOCALGC_CALLCONV)\b")

# Words that are part of a C++ type rather than a parameter name. Without these, the trailing
# word of a multi-word type such as `unsigned long` would be mistaken for a parameter name and
# dropped, which would silently map it to the wrong C# type instead of reporting that no mapping
# is recorded for it.
TYPE_KEYWORDS = frozenset(("unsigned", "signed", "long", "short", "int", "char", "float", "double", "void", "bool"))


class UnsupportedDeclaration(ValueError):
    """A declaration this script does not know how to translate."""


def strip_comments(source):
    source = re.sub(r"/\*.*?\*/", "", source, flags=re.DOTALL)
    return re.sub(r"//.*?$", "", source, flags=re.MULTILINE)


def type_body(source, kind, name):
    declaration = re.search(rf"\b{kind}\s+{re.escape(name)}\b[^;{{]*{{", source)
    if declaration is None:
        raise ValueError(f"Could not find {kind} {name}")

    open_brace = source.find("{", declaration.start())
    depth = 1
    for index in range(open_brace + 1, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[open_brace + 1:index]

    raise ValueError(f"Could not find the end of {kind} {name}")


def split_arguments(text, open_bracket="(", close_bracket=")"):
    """Splits a comma-separated list, ignoring commas nested in brackets."""
    arguments = []
    depth = 0
    current = ""
    for character in text:
        if character in (open_bracket, "("):
            depth += 1
        elif character in (close_bracket, ")"):
            depth -= 1
        if character == "," and depth == 0:
            arguments.append(current)
            current = ""
        else:
            current += character

    if current.strip():
        arguments.append(current)

    return [argument.strip() for argument in arguments if argument.strip()]


def parse_function_typedefs(source):
    """Collects the callback typedefs a GC/EE signature can refer to.

    Handles the three spellings the headers use: a function pointer type
    (`typedef R (*N)(args);`, referred to as `N`), a function type (`typedef R N(args);`,
    referred to as `N*`), and an alias declaration (`using N = R (*)(args);`).
    """
    typedefs = {}

    for match in re.finditer(r"\btypedef\s+([\w\s*]+?)\(\s*[\w\s]*\*\s*(\w+)\s*\)\s*\(([^;]*)\)\s*;", source):
        typedefs[match.group(2)] = (match.group(1), match.group(3))

    for match in re.finditer(r"\btypedef\s+([\w\s*]+?\b)(\w+)\s*\(([^;()]*)\)\s*;", source):
        # The function-pointer spelling above also matches here; it is already recorded.
        typedefs.setdefault(match.group(2), (match.group(1), match.group(3)))

    for match in re.finditer(r"\busing\s+(\w+)\s*=\s*([\w\s*]+?)\(\s*\*\s*\)\s*\(([^;]*)\)\s*;", source):
        typedefs[match.group(1)] = (match.group(2), match.group(3))

    return typedefs


def function_pointer_type(return_type, parameters, typedefs):
    """Builds the C# function-pointer type for a callback with the given C++ signature."""
    mapped = [map_type(parameter, typedefs) for parameter in split_arguments(parameters)]
    mapped = [parameter for parameter in mapped if parameter != "void"]
    mapped.append(map_type(return_type, typedefs))
    return f"{CALLBACK_CONVENTION}<{','.join(mapped)}>"


def map_type(declaration, typedefs):
    """Maps one C++ parameter or return type to the C# type the port uses for it."""
    declaration = NOISE.sub(" ", declaration).strip()

    # An inline function-pointer parameter, for example `void (*callback)(Object*, void*)`.
    inline = re.fullmatch(r"([\w\s*]+?)\(\s*\*\s*\w*\s*\)\s*\(([^)]*)\)", declaration)
    if inline is not None:
        return function_pointer_type(inline.group(1), inline.group(2), typedefs)

    # Drop any default argument, then the parameter name if the declaration carries one.
    declaration = re.sub(r"\s*=.*$", "", declaration).strip()
    words = declaration.replace("*", " * ").replace("&", " * ").split()
    if (len(words) > 1
            and re.fullmatch(r"\w+", words[-1])
            and words[-1] not in TYPES
            and words[-1] not in TYPE_KEYWORDS):
        words = words[:-1]

    stars = words.count("*")
    name = " ".join(word for word in words if word != "*")
    if not name:
        raise UnsupportedDeclaration(f"Could not parse the type of '{declaration}'")

    if name in typedefs:
        return_type, parameters = typedefs[name]
        # A function type is referred to through a pointer; a function pointer type is not.
        if stars > 1:
            raise UnsupportedDeclaration(f"Unsupported pointer depth for callback '{declaration}'")
        return function_pointer_type(return_type, parameters, typedefs)

    if name not in TYPES:
        raise UnsupportedDeclaration(f"No C# mapping is recorded for the C++ type '{name}'")

    mapped = TYPES[name]
    if mapped == "void" and stars == 0:
        return "void"

    return mapped + "*" * stars


def native_slots(source, interface, typedefs):
    """Returns the (name, signature) of every virtual slot the interface declares itself."""
    body = type_body(strip_comments(source), "class", interface)
    slots = []
    overloads = {}

    virtuals = list(re.finditer(r"\bvirtual\b", body))
    for index, match in enumerate(virtuals):
        end = virtuals[index + 1].start() if index + 1 < len(virtuals) else len(body)
        declaration = body[match.end():end]

        if re.search(rf"~\s*{re.escape(interface)}\s*\(", declaration):
            continue

        pure_virtual = re.search(r"\bPURE_VIRTUAL\b", declaration)
        if pure_virtual is None:
            raise ValueError(f"Unsupported non-pure virtual declaration in {interface}: {declaration!r}")

        declaration = declaration[:pure_virtual.start()]
        open_paren = declaration.find("(")
        close_paren = declaration.rfind(")")
        if open_paren < 0 or close_paren < open_paren:
            raise ValueError(f"Could not parse virtual declaration in {interface}: {declaration!r}")

        name_match = re.search(r"([A-Za-z_]\w*)\s*$", declaration[:open_paren])
        if name_match is None:
            raise ValueError(f"Could not parse virtual method name in {interface}: {declaration!r}")

        name = name_match.group(1)
        return_type = declaration[:name_match.start(1)]
        parameters = declaration[open_paren + 1:close_paren]

        try:
            # Every slot takes the interface pointer as its first argument, the way the C++ ABI
            # passes `this`.
            mapped = ["void*"]
            mapped += [
                parameter
                for parameter in (map_type(argument, typedefs) for argument in split_arguments(parameters))
                if parameter != "void"
            ]
            mapped.append(map_type(return_type, typedefs))
        except UnsupportedDeclaration as error:
            raise ValueError(f"{interface}::{name}: {error}") from error

        occurrence = overloads.get(name, 0) + 1
        overloads[name] = occurrence
        slots.append((name if occurrence == 1 else f"{name}_{occurrence}", mapped))

    return slots


def normalize(managed_type):
    return re.sub(r"\s+", "", managed_type)


def managed_slots(source, interface, base):
    """Returns the (name, signature, convention) of every slot the managed vtable declares."""
    body = type_body(strip_comments(source), "struct", f"{interface}Vtable")

    if base is None:
        count_match = re.search(r"\bSlotCount\s*=\s*(\d+)\s*;", body)
        if count_match is None:
            raise ValueError(f"Could not find SlotCount in {interface}Vtable")
        declared_count = int(count_match.group(1))
    else:
        count_match = re.search(
            rf"\bSlotCount\s*=\s*{re.escape(base)}Vtable\.SlotCount\s*\+\s*(\d+)\s*;", body)
        if count_match is None:
            raise ValueError(
                f"{interface}Vtable must declare SlotCount as {base}Vtable.SlotCount plus the "
                f"number of slots {interface} adds")
        declared_count = int(count_match.group(1))

        if re.search(rf"\bpublic\s+{re.escape(base)}Vtable\s+\w+\s*;", body) is None:
            raise ValueError(
                f"{interface}Vtable must embed a {base}Vtable field, because the C++ ABI places "
                f"the slots of the base interface first")

    slots = []
    for match in re.finditer(r"\bpublic\s+(delegate\*[^;]*?)\s+([A-Za-z_]\w*)\s*;", body):
        declaration = normalize(match.group(1))
        open_angle = declaration.find("<")
        if open_angle < 0 or not declaration.endswith(">"):
            raise ValueError(f"Could not parse the slot type of {interface}Vtable.{match.group(2)}")

        convention = declaration[:open_angle]
        signature = split_arguments(declaration[open_angle + 1:-1], "<", ">")
        slots.append((match.group(2), signature, convention))

    if declared_count != len(slots):
        suffix = f"slots beyond {base}Vtable" if base else "slots"
        raise ValueError(
            f"{interface}Vtable declares {declared_count} {suffix}, but contains {len(slots)} "
            f"function-pointer fields")

    return slots


def verify_interface(native_source, managed_source, interface, base, implemented_by_gc, typedefs):
    expected = native_slots(native_source, interface, typedefs)
    actual = managed_slots(managed_source, interface, base)

    convention = IMPLEMENTED_BY_GC_CONVENTION if implemented_by_gc else IMPLEMENTED_BY_EE_CONVENTION

    mismatches = []
    for index in range(max(len(expected), len(actual))):
        native_name, native_signature = expected[index] if index < len(expected) else ("<missing>", [])
        if index < len(actual):
            managed_name, managed_signature, managed_convention = actual[index]
        else:
            managed_name, managed_signature, managed_convention = "<missing>", [], convention

        if native_name != managed_name:
            mismatches.append((index, f"native {native_name}", f"managed {managed_name}"))
        elif managed_convention != convention:
            mismatches.append((index, f"{native_name} expects {convention}<...>",
                               f"declared {managed_convention}<...>"))
        elif [normalize(part) for part in native_signature] != managed_signature:
            mismatches.append((index, f"{native_name}<{', '.join(native_signature)}>",
                               f"{managed_name}<{', '.join(managed_signature)}>"))

    if not mismatches:
        return

    print(f"{interface}Vtable does not match the native interface:", file=sys.stderr)
    for index, native, managed in mismatches:
        print(f"! slot {index:2}: {native}", file=sys.stderr)
        print(f"            {managed}", file=sys.stderr)

    raise ValueError(f"{interface} vtable does not match {len(mismatches)} native slot(s)")


def main():
    parser = argparse.ArgumentParser(
        description="Verify that managed GC vtable fields match the native GC/EE interfaces."
    )
    parser.add_argument("--gcinterface", required=True, type=Path)
    parser.add_argument("--gcinterface-ee", required=True, type=Path)
    parser.add_argument("--gc", required=True, type=Path)
    parser.add_argument("--managed", required=True, type=Path)
    args = parser.parse_args()

    sources = {
        "gcinterface": args.gcinterface.read_text(encoding="utf-8"),
        "gcinterface_ee": args.gcinterface_ee.read_text(encoding="utf-8"),
        "gc": args.gc.read_text(encoding="utf-8"),
    }
    managed_source = args.managed.read_text(encoding="utf-8")

    typedefs = {}
    for source in sources.values():
        typedefs.update(parse_function_typedefs(strip_comments(source)))

    try:
        for interface, source_name, base, implemented_by_gc in INTERFACES:
            verify_interface(sources[source_name], managed_source, interface, base, implemented_by_gc, typedefs)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    print("Verified managed GC vtable slot order, signatures and calling conventions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
