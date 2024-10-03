
# The TFM that we're building
NETCOREAPP_CURRENT = "net9.0"
# The TFM used by our LKG SDK
NETCOREAPP_TOOL_CURRENT = "net9.0"

LIVE_REFPACK_DEPS = [
    # Roughly topologically sorted
    "//src/libraries:ref_System.Runtime",
    "//src/libraries:ref_System.Console",
    "//src/libraries:ref_System.Collections",
    "//src/libraries:ref_System.Collections.NonGeneric",
    "//src/libraries:ref_System.ComponentModel",
    "//src/libraries:ref_System.Diagnostics.FileVersionInfo",
    "//src/libraries:ref_System.ObjectModel",
    "//src/libraries:ref_System.ComponentModel.Primitives",
    "//src/libraries:ref_System.Collections.Specialized",
    "//src/libraries:ref_System.Runtime.InteropServices",
    "//src/libraries:ref_System.Diagnostics.Process",
]