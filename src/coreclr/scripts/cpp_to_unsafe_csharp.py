#!/usr/bin/env python3
#
## Licensed to the .NET Foundation under one or more agreements.
## The .NET Foundation licenses this file to you under the MIT license.
#

"""Transliterate selected C++ function bodies into unsafe C#.

This is intentionally a narrow source translation aid. Clang performs parsing
and type resolution; this script preserves the resulting control flow and
pointer operations while making unsupported constructs fail visibly.
"""

import argparse
import json
import pathlib
import re
import shlex
import shutil
import subprocess
import sys


FUNCTION_KINDS = {"FunctionDecl", "CXXMethodDecl"}
ASSERT_MACROS = {"assert", "_ASSERTE"}
CAST_KINDS = {
    "CStyleCastExpr",
    "CXXConstCastExpr",
    "CXXFunctionalCastExpr",
    "CXXReinterpretCastExpr",
    "CXXStaticCastExpr",
}
EXPLICIT_IMPLICIT_CAST_KINDS = {
    "BitCast",
    "BooleanToSignedIntegral",
    "FloatingCast",
    "FloatingToIntegral",
    "IntegralCast",
    "IntegralToFloating",
    "IntegralToPointer",
    "PointerToIntegral",
}
TRANSPARENT_EXPRESSION_KINDS = {
    "ConstantExpr",
    "ExprWithCleanups",
    "FullExpr",
    "MaterializeTemporaryExpr",
    "OpaqueValueExpr",
    "ParenListExpr",
}
TYPE_MAP = {
    "_Bool": "bool",
    "BOOL": "bool",
    "BYTE": "byte",
    "DWORD": "uint",
    "HRESULT": "int",
    "LONG": "int",
    "SSIZE_T": "nint",
    "UINT": "uint",
    "UINT16": "ushort",
    "UINT32": "uint",
    "UINT64": "ulong",
    "ULONG": "uint",
    "bool": "bool",
    "char": "sbyte",
    "double": "double",
    "float": "float",
    "int": "int",
    "int16_t": "short",
    "int32_t": "int",
    "int64_t": "long",
    "int8_t": "sbyte",
    "intptr_t": "nint",
    "long": "long",
    "long long": "long",
    "ptrdiff_t": "nint",
    "short": "short",
    "signed char": "sbyte",
    "size_t": "nuint",
    "uint16_t": "ushort",
    "uint32_t": "uint",
    "uint64_t": "ulong",
    "uint8_t": "byte",
    "uintptr_t": "nuint",
    "unsigned char": "byte",
    "unsigned int": "uint",
    "unsigned long": "ulong",
    "unsigned long long": "ulong",
    "unsigned short": "ushort",
    "void": "void",
    "wchar_t": "uint",
}


class TranslationError(Exception):
    pass


def _children(node):
    return [child for child in node.get("inner", []) if child.get("kind")]


def _parse_mapping(value):
    source, separator, target = value.partition("=")
    if not separator or not source or not target:
        raise argparse.ArgumentTypeError("mapping must have the form source=target")
    return source, target


def _read_json_values(text):
    decoder = json.JSONDecoder()
    values = []
    offset = 0
    while offset < len(text):
        while offset < len(text) and text[offset].isspace():
            offset += 1
        if offset == len(text):
            break
        value, offset = decoder.raw_decode(text, offset)
        values.append(value)
    return values


def _find_clang(requested):
    if requested:
        resolved = shutil.which(requested)
        if resolved:
            return resolved
        raise TranslationError(f"unable to find Clang executable '{requested}'")

    for candidate in ("clang", "clang-20", "clang-19", "clang-18", "clang-17"):
        resolved = shutil.which(candidate)
        if resolved:
            return resolved
    raise TranslationError("unable to find clang; pass --clang with an executable path")


def load_compile_arguments(compile_commands, source, compile_output=None):
    compile_commands = pathlib.Path(compile_commands)
    entries = json.loads(compile_commands.read_text(encoding="utf-8"))
    source = source.resolve()
    matches = [
        entry
        for entry in entries
        if (
            pathlib.Path(entry["file"])
            if pathlib.Path(entry["file"]).is_absolute()
            else pathlib.Path(entry["directory"]) / entry["file"]
        ).resolve()
        == source
    ]
    if compile_output:
        matches = [
            entry
            for entry in matches
            if compile_output in entry.get("output", "")
        ]
    if len(matches) != 1:
        outputs = ", ".join(
            entry.get("output", "<no output>") for entry in matches
        )
        raise TranslationError(
            f"expected one compile command for '{source}', found {len(matches)}"
            + (f": {outputs}" if outputs else "")
        )

    entry = matches[0]
    working_directory = pathlib.Path(entry["directory"])
    arguments = entry.get("arguments")
    if arguments is None:
        arguments = shlex.split(entry["command"])
    arguments = arguments[1:]

    filtered = []
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        argument_path = (
            (working_directory / argument).resolve()
            if not argument.startswith("-")
            else None
        )
        if argument == "-c" or argument_path == source:
            index += 1
            continue
        if argument in {"-o", "-MF", "-MT", "-MQ"}:
            index += 2
            continue
        filtered.append(argument)
        index += 1

    return working_directory, filtered


def load_clang_ast(
    source,
    function_filter,
    clang,
    clang_arguments,
    compile_commands=None,
    compile_output=None,
):
    working_directory = None
    if compile_commands:
        working_directory, compile_arguments = load_compile_arguments(
            compile_commands,
            source,
            compile_output=compile_output,
        )
        clang_arguments = [*compile_arguments, *clang_arguments]

    command = [
        _find_clang(clang),
        "-Xclang",
        "-ast-dump=json",
        "-Xclang",
        f"-ast-dump-filter={function_filter}",
        "-fsyntax-only",
        *clang_arguments,
        str(source),
    ]
    result = subprocess.run(
        command,
        capture_output=True,
        check=False,
        cwd=working_directory,
        text=True,
    )
    if result.returncode != 0:
        raise TranslationError(
            f"Clang failed with exit code {result.returncode}:\n{result.stderr.rstrip()}"
        )
    if not result.stdout.strip():
        raise TranslationError(f"Clang produced no AST for filter '{function_filter}'")
    try:
        return _read_json_values(result.stdout)
    except json.JSONDecodeError as error:
        raise TranslationError(f"unable to parse Clang AST JSON: {error}") from error


def find_function(ast_roots, requested_name):
    leaf_name = requested_name.rsplit("::", 1)[-1]
    matches = []

    def visit(node):
        if node.get("kind") in FUNCTION_KINDS and node.get("name") == leaf_name:
            if any(child.get("kind") == "CompoundStmt" for child in _children(node)):
                matches.append(node)
        for child in _children(node):
            visit(child)

    for root in ast_roots:
        visit(root)

    if not matches:
        raise TranslationError(f"unable to find a definition for function '{requested_name}'")
    if len(matches) != 1:
        signatures = ", ".join(match.get("type", {}).get("qualType", "<unknown>") for match in matches)
        raise TranslationError(
            f"function filter '{requested_name}' matched {len(matches)} definitions: {signatures}"
        )
    return matches[0]


class CSharpEmitter:
    def __init__(self, source_path, source_text, type_map=None, symbol_map=None, accessibility="private"):
        self.source_path = source_path
        self.source_text = source_text
        self.type_map = dict(TYPE_MAP)
        self.type_map.update(type_map or {})
        self.symbol_map = symbol_map or {}
        self.accessibility = accessibility
        self.unsupported = []
        self._indent = 0
        self._lines = []
        self._labels = {}

    def emit_function(self, function, method_name=None):
        self.unsupported.clear()
        self._lines.clear()
        self._labels.clear()
        self._indent = 0

        body = next(
            (child for child in _children(function) if child.get("kind") == "CompoundStmt"),
            None,
        )
        if body is None:
            raise TranslationError("selected declaration has no function body")
        self._collect_labels(body)

        parameters = [
            child for child in _children(function) if child.get("kind") == "ParmVarDecl"
        ]
        signature_type = function.get("type", {}).get("qualType", "")
        return_type = self._return_type(signature_type)
        translated_name = method_name or self._symbol(function.get("name", "TranslatedFunction"))
        parameter_text = ", ".join(self._parameter(parameter) for parameter in parameters)
        location = self._location(function)

        self._write(f"// C++ source: {self.source_path}:{location}")
        self._write("// Mechanical transliteration; review all unsupported markers and integer conversions.")
        self._write(
            f"{self.accessibility} static unsafe {return_type} {translated_name}({parameter_text})"
        )
        self._emit_compound(body)

        if self.unsupported:
            kinds = ", ".join(sorted(set(self.unsupported)))
            self._lines.insert(0, f"#error GC transliteration contains unsupported AST nodes: {kinds}")

        return "\n".join(self._lines) + "\n"

    def _parameter(self, node):
        name = self._symbol(node.get("name", "unnamed"))
        type_name = self._type(node.get("type", {}).get("qualType", "void"))
        return f"{type_name} {name}"

    def _return_type(self, signature):
        depth = 0
        for index, character in enumerate(signature):
            if character == "(":
                if depth == 0:
                    return self._type(signature[:index].strip())
                depth += 1
            elif character == ")":
                depth -= 1
        raise TranslationError(f"unable to determine return type from '{signature}'")

    def _type(self, type_name):
        original = type_name.strip()
        if not original:
            return "void"
        if "(*)" in original or "(&)" in original:
            return self._unsupported_type(original)
        if re.search(r"\[[^\]]*\]$", original):
            return self._unsupported_type(original)

        normalized = re.sub(r"\b(const|volatile|restrict|__restrict|__restrict__)\b", "", original)
        normalized = re.sub(r"\b(class|struct|enum)\s+", "", normalized)
        normalized = re.sub(r"\s+", " ", normalized).strip()

        if normalized.endswith("&&") or normalized.endswith("&"):
            return self._unsupported_type(original)

        pointer_count = 0
        while normalized.endswith("*"):
            pointer_count += 1
            normalized = normalized[:-1].rstrip()

        base_type = self.type_map.get(normalized, normalized.replace("::", "."))
        return base_type + ("*" * pointer_count)

    def _unsupported_type(self, type_name):
        marker = f"__GC_TRANSLATION_UNSUPPORTED_TYPE_{self._identifier(type_name)}"
        self.unsupported.append(f"type:{type_name}")
        return marker

    def _emit_compound(self, node):
        self._write("{")
        self._indent += 1
        for child in _children(node):
            self._emit_statement(child)
        self._indent -= 1
        self._write("}")

    def _emit_statement(self, node):
        kind = node.get("kind")
        macro_name = self._expansion_macro_name(node)
        if macro_name in ASSERT_MACROS:
            self._emit_assert(node)
            return

        if kind == "CompoundStmt":
            self._emit_compound(node)
        elif kind == "DeclStmt":
            for declaration in _children(node):
                self._emit_declaration(declaration)
        elif kind == "IfStmt":
            self._emit_if(node)
        elif kind == "ForStmt":
            self._emit_for(node)
        elif kind == "WhileStmt":
            children = _children(node)
            if node.get("hasVar") or len(children) != 2:
                self._unsupported_statement(node)
                return
            self._write(f"while ({self._expression(children[0])})")
            self._emit_embedded(children[1])
        elif kind == "DoStmt":
            children = _children(node)
            if len(children) != 2:
                self._unsupported_statement(node)
                return
            self._write("do")
            self._emit_embedded(children[0])
            self._write(f"while ({self._expression(children[1])});")
        elif kind == "SwitchStmt":
            children = _children(node)
            if node.get("hasInit") or node.get("hasVar") or len(children) != 2:
                self._unsupported_statement(node)
                return
            self._write(f"switch ({self._expression(children[0])})")
            self._emit_embedded(children[1])
        elif kind in {"CaseStmt", "DefaultStmt"}:
            self._emit_case(node)
        elif kind == "ReturnStmt":
            children = _children(node)
            self._write("return;" if not children else f"return {self._expression(children[0])};")
        elif kind == "BreakStmt":
            self._write("break;")
        elif kind == "ContinueStmt":
            self._write("continue;")
        elif kind == "GotoStmt":
            target = self._labels.get(node.get("targetLabelDeclId"), "unknown_label")
            self._write(f"goto {self._symbol(target)};")
        elif kind == "LabelStmt":
            self._indent -= 1
            self._write(f"{self._symbol(node.get('name', 'unknown_label'))}:")
            self._indent += 1
            children = _children(node)
            if children:
                self._emit_statement(children[0])
        elif kind == "NullStmt":
            self._write(";")
        elif kind in {
            "BinaryOperator",
            "CallExpr",
            "CompoundAssignOperator",
            "CXXMemberCallExpr",
            "CXXOperatorCallExpr",
            "UnaryOperator",
        }:
            self._write(f"{self._expression(node)};")
        else:
            self._unsupported_statement(node)

    def _emit_assert(self, node):
        children = _children(node)
        if (
            node.get("kind") != "ParenExpr"
            or len(children) != 1
            or children[0].get("kind") != "ConditionalOperator"
        ):
            self._unsupported_statement(node)
            return

        conditional_children = _children(children[0])
        if not conditional_children:
            self._unsupported_statement(node)
            return

        condition = conditional_children[0]
        if condition.get("kind") in CAST_KINDS:
            cast_children = _children(condition)
            if cast_children:
                condition = cast_children[-1]
        self._write(f"if (!({self._expression(condition)}))")
        self._write("{")
        self._indent += 1
        self._write("GCToOSInterface.DebugBreak();")
        self._indent -= 1
        self._write("}")

    def _emit_declaration(self, node, terminate=True):
        if node.get("kind") != "VarDecl":
            text = self._unsupported_expression(node)
            if terminate:
                self._write(f"{text};")
            return text

        type_name = self._type(node.get("type", {}).get("qualType", "void"))
        name = self._symbol(node.get("name", "unnamed"))
        children = _children(node)
        text = f"{type_name} {name}"
        if children:
            text += f" = {self._expression(children[-1])}"
        if terminate:
            self._write(f"{text};")
        return text

    def _emit_if(self, node):
        children = _children(node)
        if node.get("hasInit") or node.get("hasVar") or len(children) < 2:
            self._unsupported_statement(node)
            return

        condition = children[0]
        then_statement = children[1]
        self._write(f"if ({self._expression(condition)})")
        self._emit_embedded(then_statement)
        if len(children) > 2:
            self._write("else")
            self._emit_embedded(children[2])

    def _emit_for(self, node):
        raw_children = node.get("inner", [])
        if not raw_children:
            self._unsupported_statement(node)
            return

        body = next(
            (child for child in reversed(raw_children) if child.get("kind")),
            None,
        )
        if body is None:
            self._unsupported_statement(node)
            return

        header_nodes = raw_children[: raw_children.index(body)]
        while len(header_nodes) < 4:
            header_nodes.append({})
        if len(header_nodes) > 4:
            self._unsupported_statement(node)
            return

        initializer, condition_variable, condition, increment = header_nodes
        if condition_variable.get("kind"):
            self._unsupported_statement(node)
            return

        initializer_text = ""
        if initializer.get("kind") == "DeclStmt":
            declarations = _children(initializer)
            if len(declarations) != 1:
                self._unsupported_statement(node)
                return
            initializer_text = self._emit_declaration(declarations[0], terminate=False)
        elif initializer.get("kind"):
            initializer_text = self._expression(initializer)

        condition_text = self._expression(condition) if condition.get("kind") else ""
        increment_text = self._expression(increment) if increment.get("kind") else ""
        self._write(f"for ({initializer_text}; {condition_text}; {increment_text})")
        self._emit_embedded(body)

    def _emit_case(self, node):
        children = _children(node)
        if node.get("kind") == "CaseStmt":
            if not children:
                self._unsupported_statement(node)
                return
            self._indent -= 1
            self._write(f"case {self._expression(children[0])}:")
            self._indent += 1
            children = children[1:]
        else:
            self._indent -= 1
            self._write("default:")
            self._indent += 1
        for child in children:
            self._emit_statement(child)

    def _emit_embedded(self, node):
        if node.get("kind") == "CompoundStmt":
            self._emit_compound(node)
            return
        self._write("{")
        self._indent += 1
        self._emit_statement(node)
        self._indent -= 1
        self._write("}")

    def _expression(self, node):
        kind = node.get("kind")
        children = _children(node)

        if kind in TRANSPARENT_EXPRESSION_KINDS:
            return self._expression(children[-1]) if children else "default"
        if kind == "ImplicitCastExpr":
            cast_kind = node.get("castKind")
            expression = self._expression(children[-1]) if children else "default"
            if cast_kind == "PointerToBoolean":
                return f"({expression} is not null)"
            if cast_kind == "IntegralToBoolean":
                return f"({expression} != 0)"
            if cast_kind == "NullToPointer":
                return "null"
            if cast_kind in EXPLICIT_IMPLICIT_CAST_KINDS:
                target_type = self._type(node.get("type", {}).get("qualType", "void"))
                return f"(({target_type}){expression})"
            if cast_kind == "ToVoid":
                return self._unsupported_expression(node)
            return expression
        if kind == "ParenExpr":
            return f"({self._expression(children[0])})"
        if kind == "IntegerLiteral":
            return node.get("value", "0")
        if kind == "FloatingLiteral":
            value = node.get("value", "0")
            return value + "f" if node.get("type", {}).get("qualType") == "float" else value
        if kind == "CharacterLiteral":
            type_name = self._type(node.get("type", {}).get("qualType", "char"))
            return f"({type_name}){node.get('value', '0')}"
        if kind == "StringLiteral":
            return json.dumps(node.get("value", ""))
        if kind == "CXXBoolLiteralExpr":
            return "true" if node.get("value") else "false"
        if kind in {"CXXNullPtrLiteralExpr", "GNUNullExpr"}:
            return "null"
        if kind == "ImplicitValueInitExpr":
            return "default"
        if kind == "CXXThisExpr":
            return "this"
        if kind == "DeclRefExpr":
            declaration = node.get("referencedDecl", {})
            return self._symbol(declaration.get("name", node.get("name", "unknown")))
        if kind == "MemberExpr":
            member_name = self._symbol(node.get("name", "unknown_member"))
            if not children or children[0].get("kind") == "CXXThisExpr":
                return member_name
            base = self._expression(children[0])
            operator = "->" if node.get("isArrow") else "."
            return f"{base}{operator}{member_name}"
        if kind in {"BinaryOperator", "CompoundAssignOperator"}:
            return (
                f"({self._expression(children[0])} {node.get('opcode', '?')} "
                f"{self._expression(children[1])})"
            )
        if kind == "UnaryOperator":
            operand = self._expression(children[0])
            opcode = node.get("opcode", "?")
            return f"({operand}{opcode})" if node.get("isPostfix") else f"({opcode}{operand})"
        if kind == "ConditionalOperator":
            return (
                f"({self._expression(children[0])} ? {self._expression(children[1])} : "
                f"{self._expression(children[2])})"
            )
        if kind == "ArraySubscriptExpr":
            return f"{self._expression(children[0])}[{self._expression(children[1])}]"
        if kind in {"CallExpr", "CXXMemberCallExpr"}:
            callee = self._expression(children[0])
            arguments = ", ".join(self._expression(child) for child in children[1:])
            return f"{callee}({arguments})"
        if kind == "CXXOperatorCallExpr":
            return self._unsupported_expression(node)
        if kind in CAST_KINDS:
            target_type = self._type(node.get("type", {}).get("qualType", "void"))
            return f"(({target_type}){self._expression(children[-1])})"
        if kind == "UnaryExprOrTypeTraitExpr":
            if node.get("name") != "sizeof":
                return self._unsupported_expression(node)
            argument_type = node.get("argType", {}).get("qualType")
            if argument_type:
                return f"sizeof({self._type(argument_type)})"
            return f"sizeof({self._expression(children[0])})"
        if kind == "CXXDefaultArgExpr" and children:
            return self._expression(children[-1])

        return self._unsupported_expression(node)

    def _unsupported_statement(self, node):
        kind = node.get("kind", "Unknown")
        self.unsupported.append(kind)
        snippet = self._source_snippet(node)
        self._write(
            f'__gc_translation_unsupported_statement("{kind}", {json.dumps(snippet)});'
        )

    def _unsupported_expression(self, node):
        kind = node.get("kind", "Unknown")
        self.unsupported.append(kind)
        snippet = self._source_snippet(node)
        return f'__gc_translation_unsupported_expression("{kind}", {json.dumps(snippet)})'

    def _source_snippet(self, node):
        source_range = node.get("range", {})
        begin_info = source_range.get("begin", {})
        end_info = source_range.get("end", {})
        begin_expansion = begin_info.get("expansionLoc")
        end_expansion = end_info.get("expansionLoc")
        if begin_expansion and end_expansion:
            begin_info = begin_expansion
            end_info = end_expansion

        begin = begin_info.get("offset")
        end = end_info.get("offset")
        if begin is None or end is None:
            return ""
        end += end_info.get("tokLen", 0)
        return self.source_text[begin:end].strip().replace("\n", " ")[:160]

    def _expansion_macro_name(self, node):
        begin = node.get("range", {}).get("begin", {})
        expansion = begin.get("expansionLoc")
        if not expansion:
            return None
        offset = expansion.get("offset")
        token_length = expansion.get("tokLen")
        if offset is None or token_length is None:
            return None
        return self.source_text[offset : offset + token_length]

    def _collect_labels(self, node):
        if node.get("kind") == "LabelStmt" and node.get("declId"):
            self._labels[node["declId"]] = node.get("name", "unknown_label")
        for child in _children(node):
            self._collect_labels(child)

    def _location(self, node):
        location = node.get("loc", {})
        return location.get("line", "?")

    def _symbol(self, name):
        return self.symbol_map.get(name, name.replace("::", "."))

    @staticmethod
    def _identifier(value):
        return re.sub(r"[^A-Za-z0-9_]", "_", value)

    def _write(self, text):
        self._lines.append(("    " * self._indent) + text)


def translate(
    source,
    function_name,
    clang=None,
    clang_arguments=None,
    compile_commands=None,
    compile_output=None,
    type_map=None,
    symbol_map=None,
    accessibility="private",
    method_name=None,
):
    source = pathlib.Path(source).resolve()
    source_text = source.read_text(encoding="utf-8")
    ast_roots = load_clang_ast(
        source,
        function_name,
        clang,
        (
            [] if compile_commands else ["-x", "c++", "-std=c++17"]
        ) if clang_arguments is None else clang_arguments,
        compile_commands=compile_commands,
        compile_output=compile_output,
    )
    function = find_function(ast_roots, function_name)
    emitter = CSharpEmitter(
        source,
        source_text,
        type_map=dict(type_map or []),
        symbol_map=dict(symbol_map or []),
        accessibility=accessibility,
    )
    output = emitter.emit_function(function, method_name=method_name)
    return output, emitter.unsupported


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Transliterate one Clang-parsed C++ function body into unsafe C#."
    )
    parser.add_argument("--source", required=True, type=pathlib.Path)
    parser.add_argument("--function", required=True)
    parser.add_argument("--clang")
    parser.add_argument("--clang-arg", action="append", default=[])
    parser.add_argument(
        "--compile-commands",
        type=pathlib.Path,
        help="Reuse the matching entry from a CMake compile_commands.json file.",
    )
    parser.add_argument(
        "--compile-output",
        help="Select a compile database entry by an output-path substring.",
    )
    parser.add_argument("--type-map", action="append", default=[], type=_parse_mapping)
    parser.add_argument("--symbol-map", action="append", default=[], type=_parse_mapping)
    parser.add_argument("--method-name")
    parser.add_argument(
        "--accessibility",
        choices=("private", "internal", "public"),
        default="private",
    )
    parser.add_argument("--output", type=pathlib.Path)
    arguments = parser.parse_args(argv)

    clang_arguments = (
        arguments.clang_arg
        if arguments.compile_commands
        else ["-x", "c++", "-std=c++17", *arguments.clang_arg]
    )
    try:
        output, unsupported = translate(
            arguments.source,
            arguments.function,
            clang=arguments.clang,
            clang_arguments=clang_arguments,
            compile_commands=arguments.compile_commands,
            compile_output=arguments.compile_output,
            type_map=arguments.type_map,
            symbol_map=arguments.symbol_map,
            accessibility=arguments.accessibility,
            method_name=arguments.method_name,
        )
    except (OSError, TranslationError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    if arguments.output:
        arguments.output.write_text(output, encoding="utf-8")
    else:
        sys.stdout.write(output)
    return 2 if unsupported else 0


if __name__ == "__main__":
    sys.exit(main())
