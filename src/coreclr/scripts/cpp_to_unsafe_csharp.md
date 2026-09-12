# C++ to unsafe C# GC transliterator

`cpp_to_unsafe_csharp.py` mechanically translates a selected C++ function body from Clang's
semantic AST into unsafe C#. It is a translation aid, not a C++ compiler. Generated code must
be reviewed against the original source.

The tool is designed to reuse an existing CoreCLR CMake compilation database so that Clang
sees the same target, feature defines, include paths, and preprocessing branches as the native
GC build:

```bash
python3 src/coreclr/scripts/cpp_to_unsafe_csharp.py \
    --source src/coreclr/gc/mark_phase.cpp \
    --function gc_heap::mark_phase \
    --clang clang-20 \
    --compile-commands artifacts/obj/coreclr/linux.x64.Checked/compile_commands.json \
    --compile-output clrgc_gc_wks \
    --output artifacts/mark_phase.cs
```

`--compile-output` selects a compilation database variant when a source file is compiled more
than once. `clrgc_gc_wks` selects the standalone workstation GC configuration.

The emitter preserves statement order, branches, loops, labels, pointer arithmetic, casts,
member access, and assertion sites. Assertions use the collector's unmanaged
`GCToOSInterface.DebugBreak` path rather than managed diagnostics. Common native integer types
are mapped to C# types.
Additional mechanical mappings can be supplied with repeated options:

```bash
--type-map native_type=ManagedType
--symbol-map native_name=ManagedName
```

Unsupported AST nodes produce undefined `__gc_translation_unsupported_*` calls and a leading
`#error` directive. The process exits with code 2 after writing the partial translation. This
is intentional: unsupported C++ constructors, references, RAII, overloaded operators, logging
temporaries, and other semantic gaps must not be silently approximated.

Source comments are not present in Clang's statement AST and are not copied into the generated
body. Keep the native source beside the generated output during review.

Run the integration tests with:

```bash
python3 -m unittest -v src/coreclr/scripts/tests/test_cpp_to_unsafe_csharp.py
```
