#!/usr/bin/env python3
#
## Licensed to the .NET Foundation under one or more agreements.
## The .NET Foundation licenses this file to you under the MIT license.
#

import importlib.util
import json
import pathlib
import shlex
import tempfile
import unittest


SCRIPT_PATH = pathlib.Path(__file__).parents[1] / "cpp_to_unsafe_csharp.py"
SPEC = importlib.util.spec_from_file_location("cpp_to_unsafe_csharp", SCRIPT_PATH)
TRANSLATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRANSLATOR)


class CppToUnsafeCSharpTests(unittest.TestCase):
    def translate(self, source, function):
        with tempfile.TemporaryDirectory() as temporary_directory:
            source_path = pathlib.Path(temporary_directory) / "input.cpp"
            source_path.write_text(source, encoding="utf-8")
            return TRANSLATOR.translate(
                source_path,
                function,
                clang="clang-20",
                clang_arguments=["-x", "c++", "-std=c++17"],
            )

    def test_translates_pointer_control_flow(self):
        output, unsupported = self.translate(
            """
struct Node
{
    int value;
    Node* next;
};

int update(Node* node, int delta)
{
    int result = node->value;
    if (result < delta)
    {
        result += delta;
    }
    else
    {
        result--;
    }

    for (int i = 0; i < delta; i++)
    {
        result += i;
    }

    while (result > 100)
    {
        result -= 10;
    }

    return result;
}
""",
            "update",
        )

        self.assertEqual([], unsupported)
        self.assertIn("private static unsafe int update(Node* node, int delta)", output)
        self.assertIn("int result = node->value;", output)
        self.assertIn("if ((result < delta))", output)
        self.assertIn("for (int i = 0; (i < delta); (i++))", output)
        self.assertIn("while ((result > 100))", output)
        self.assertIn("return result;", output)

    def test_translates_switch_and_goto(self):
        output, unsupported = self.translate(
            """
int choose(int value)
{
retry:
    switch (value)
    {
        case 0:
            return 10;
        case 1:
            value--;
            goto retry;
        default:
            break;
    }

    return value;
}
""",
            "choose",
        )

        self.assertEqual([], unsupported)
        self.assertIn("retry:", output)
        self.assertIn("case 0:", output)
        self.assertIn("goto retry;", output)
        self.assertIn("default:", output)

    def test_removes_implicit_this(self):
        output, unsupported = self.translate(
            """
struct Heap
{
    int count;

    int add(int value)
    {
        count += value;
        return count;
    }
};
""",
            "Heap::add",
        )

        self.assertEqual([], unsupported)
        self.assertIn("(count += value);", output)
        self.assertNotIn("this.count", output)

    def test_maps_gc_integer_types(self):
        output, unsupported = self.translate(
            """
#include <stddef.h>
#include <stdint.h>

size_t advance(uint8_t* start, uintptr_t distance)
{
    return (size_t)(start + distance);
}
""",
            "advance",
        )

        self.assertEqual([], unsupported)
        self.assertIn("private static unsafe nuint advance(byte* start, nuint distance)", output)
        self.assertIn("return ((nuint)((start + distance)));", output)

    def test_translates_assert_macro(self):
        output, unsupported = self.translate(
            """
#include <assert.h>

int require_pointer(int* value)
{
    assert(value != 0);
    return *value;
}
""",
            "require_pointer",
        )

        self.assertEqual([], unsupported)
        self.assertIn("if (!((value != null)))", output)
        self.assertIn("GCToOSInterface.DebugBreak();", output)

    def test_translates_pointer_boolean_and_null(self):
        output, unsupported = self.translate(
            """
struct Node
{
    Node* next;
};

Node* next_or_null(Node* node)
{
    if (node)
    {
        return node->next;
    }

    return 0;
}
""",
            "next_or_null",
        )

        self.assertEqual([], unsupported)
        self.assertIn("if ((node is not null))", output)
        self.assertIn("return null;", output)

    def test_preserves_implicit_integer_conversion(self):
        output, unsupported = self.translate(
            """
unsigned int narrow(unsigned long value)
{
    return value;
}
""",
            "narrow",
        )

        self.assertEqual([], unsupported)
        self.assertIn("return ((uint)value);", output)

    def test_marks_unsupported_constructs(self):
        output, unsupported = self.translate(
            """
int invoke_lambda(int value)
{
    auto callback = [value](int other) { return value + other; };
    return callback(1);
}
""",
            "invoke_lambda",
        )

        self.assertNotEqual([], unsupported)
        self.assertTrue(output.startswith("#error GC transliteration contains unsupported AST nodes:"))
        self.assertIn("__gc_translation_unsupported_expression", output)

    def test_marks_scoped_condition_declaration_unsupported(self):
        output, unsupported = self.translate(
            """
int scoped_condition(int value)
{
    if (int copy = value)
    {
        return copy;
    }

    return 0;
}
""",
            "scoped_condition",
        )

        self.assertIn("IfStmt", unsupported)
        self.assertTrue(output.startswith("#error GC transliteration contains unsupported AST nodes:"))

    def test_reuses_compile_commands_entry(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = pathlib.Path(temporary_directory)
            source_path = directory / "input.cpp"
            source_path.write_text(
                """
#ifndef SELECTED_VALUE
#error SELECTED_VALUE must come from the compile command
#endif

int selected()
{
    return SELECTED_VALUE;
}
""",
                encoding="utf-8",
            )
            command = [
                "clang++-20",
                "-DSELECTED_VALUE=42",
                "-std=c++17",
                "-o",
                "input.cpp.o",
                "-c",
                "input.cpp",
            ]
            compile_commands = directory / "compile_commands.json"
            compile_commands.write_text(
                json.dumps(
                    [
                        {
                            "directory": str(directory),
                            "command": shlex.join(command),
                            "file": "input.cpp",
                            "output": "input.cpp.o",
                        }
                    ]
                ),
                encoding="utf-8",
            )

            output, unsupported = TRANSLATOR.translate(
                source_path,
                "selected",
                clang="clang-20",
                clang_arguments=[],
                compile_commands=compile_commands,
            )

        self.assertEqual([], unsupported)
        self.assertIn("return 42;", output)


if __name__ == "__main__":
    unittest.main()
