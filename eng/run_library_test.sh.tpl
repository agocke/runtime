#!/usr/bin/env bash

# --- begin runfiles.bash initialization v3 ---
# Copy-pasted from the Bazel Bash runfiles library v3.
set -uo pipefail; set +e; f=bazel_tools/tools/bash/runfiles/runfiles.bash
source "${RUNFILES_DIR:-/dev/null}/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "${RUNFILES_MANIFEST_FILE:-/dev/null}" | cut -f2- -d' ')" 2>/dev/null || \
  source "$0.runfiles/$f" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  source "$(grep -sm1 "^$f " "$0.exe.runfiles_manifest" | cut -f2- -d' ')" 2>/dev/null || \
  { echo>&2 "ERROR: cannot find $f"; exit 1; }; f=; set -e
# --- end runfiles.bash initialization v3 ---

# Run the dotnet binary from the testhost directory. The testhost contains a
# shared framework built from Bazel-built (live) assemblies, matching how
# MSBuild's testhost uses live-built bits. Because dotnet resolves frameworks
# relative to its own location, using the testhost's copy ensures our
# DEBUG-built assemblies are loaded instead of the SDK's RELEASE versions.
TESTHOST=$(rlocation TEMPLATED_testhost)

"$TESTHOST/dotnet" exec "$(rlocation TEMPLATED_xunit_console)" "$(rlocation TEMPLATED_entry_dll)" -nologo -notrait "category=failing" "$@"
