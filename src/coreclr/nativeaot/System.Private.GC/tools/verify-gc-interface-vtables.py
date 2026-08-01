#!/usr/bin/env python3

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

import argparse
from pathlib import Path
import re
import sys


INTERFACES = (
    ("IGCHandleStore", "gcinterface"),
    ("IGCHandleManager", "gcinterface"),
    ("IGCHeap", "gcinterface"),
    ("IGCToCLR", "gcinterface_ee"),
    ("IGCToCLREventSink", "gcinterface_ee"),
)


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


def native_slots(source, interface):
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
        if open_paren < 0:
            raise ValueError(f"Could not parse virtual declaration in {interface}: {declaration!r}")

        name_match = re.search(r"([A-Za-z_]\w*)\s*$", declaration[:open_paren])
        if name_match is None:
            raise ValueError(f"Could not parse virtual method name in {interface}: {declaration!r}")

        name = name_match.group(1)
        occurrence = overloads.get(name, 0) + 1
        overloads[name] = occurrence
        slots.append(name if occurrence == 1 else f"{name}_{occurrence}")

    return slots


def managed_slots(source, interface):
    body = type_body(strip_comments(source), "struct", f"{interface}Vtable")

    count_match = re.search(r"\bSlotCount\s*=\s*(\d+)\s*;", body)
    if count_match is None:
        raise ValueError(f"Could not find SlotCount in {interface}Vtable")

    slots = re.findall(r"\bpublic\s+delegate\*[^;]*\s+([A-Za-z_]\w*)\s*;", body)
    declared_count = int(count_match.group(1))
    if declared_count != len(slots):
        raise ValueError(
            f"{interface}Vtable declares SlotCount = {declared_count}, "
            f"but contains {len(slots)} function-pointer fields"
        )

    return slots


def verify_interface(native_source, managed_source, interface):
    expected = native_slots(native_source, interface)
    actual = managed_slots(managed_source, interface)
    if expected == actual:
        return

    print(f"{interface}Vtable does not match the virtual method order in the native interface:", file=sys.stderr)
    width = max(len(expected), len(actual))
    for index in range(width):
        native = expected[index] if index < len(expected) else "<missing>"
        managed = actual[index] if index < len(actual) else "<missing>"
        marker = " " if native == managed else "!"
        print(f"{marker} slot {index:2}: native {native:<48} managed {managed}", file=sys.stderr)

    raise ValueError(f"{interface} vtable slot order differs")


def main():
    parser = argparse.ArgumentParser(
        description="Verify that managed GC vtable fields match the native GC/EE interfaces."
    )
    parser.add_argument("--gcinterface", required=True, type=Path)
    parser.add_argument("--gcinterface-ee", required=True, type=Path)
    parser.add_argument("--managed", required=True, type=Path)
    args = parser.parse_args()

    sources = {
        "gcinterface": args.gcinterface.read_text(encoding="utf-8"),
        "gcinterface_ee": args.gcinterface_ee.read_text(encoding="utf-8"),
    }
    managed_source = args.managed.read_text(encoding="utf-8")

    try:
        for interface, source_name in INTERFACES:
            verify_interface(sources[source_name], managed_source, interface)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    print("Verified managed GC vtable slot order and count.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
