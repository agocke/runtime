"""Strip debug symbols from shared libraries.

Matches CMake's strip_symbols() from eng/native/functions.cmake:
  objcopy --only-keep-debug  =>  .so.dbg
  objcopy --strip-debug --strip-unneeded  =>  stripped .so
  objcopy --add-gnu-debuglink  =>  link .so to .dbg
"""

def strip_symbols(name, src, visibility = None):
    """Produces a stripped shared library and a separate .dbg symbol file.

    Outputs:
        {name}_stripped.so       - stripped shared library with .gnu_debuglink
        {name}_stripped.so.dbg   - debug symbols
    """
    native.genrule(
        name = name,
        srcs = [src],
        outs = [name + "_stripped.so", name + "_stripped.so.dbg"],
        cmd = " && ".join([
            "cp $< $(location {name}_stripped.so)".format(name = name),
            "chmod u+w $(location {name}_stripped.so)".format(name = name),
            "$(OBJCOPY) --only-keep-debug $(location {name}_stripped.so) $(location {name}_stripped.so.dbg)".format(name = name),
            "$(OBJCOPY) --strip-debug --strip-unneeded $(location {name}_stripped.so)".format(name = name),
            "$(OBJCOPY) --add-gnu-debuglink=$(location {name}_stripped.so.dbg) $(location {name}_stripped.so)".format(name = name),
        ]),
        toolchains = ["@rules_cc//cc:current_cc_toolchain"],
        visibility = visibility,
    )
